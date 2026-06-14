using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using eBackup.Core.Abstractions;
using eBackup.Core.Crypto;
using eBackup.Core.Model;
using eBackup.Core.Modules;
using eBackup.Core.Paths;
using Microsoft.Extensions.FileSystemGlobbing;

namespace eBackup.Core.Engine;

/// <summary>
/// Собирает архивы .ebk из модулей и восстанавливает их обратно по манифесту.
///
/// Формат v1: ZIP-контейнер — manifest.json в корне + data/&lt;module&gt;/... .
/// Опциональное шифрование (AES-256-GCM поверх готового ZIP) поддержано (см. ArchiveCipher).
/// </summary>
public sealed class BackupEngine
{
    /// <summary>
    /// Создать архив из набора модулей. Возвращает путь к готовому .ebk.
    /// </summary>
    /// <param name="progress">Крупные фазы — для статус-строки UI.</param>
    /// <param name="log">Детальный лог: каждый файл, размеры, пропуски, тайминги.
    /// Может вызываться с рабочего потока — получатель должен быть потокобезопасен.</param>
    public async Task<string> CreateBackupAsync(
        IEnumerable<IBackupModule> modules,
        string outputDirectory,
        string archiveName,
        string? passphrase = null,
        IProgress<string>? progress = null,
        CompressionLevel compression = CompressionLevel.Optimal,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var archivePath = Path.Combine(outputDirectory, archiveName + ".ebk");
        // При шифровании сначала собираем ZIP во временный файл, затем шифруем в .ebk.
        var buildPath = passphrase is null ? archivePath : archivePath + ".plain";

        var manifest = new Manifest
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Source = new SourceMachine
            {
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                OsVersion = Environment.OSVersion.VersionString
            }
        };

        log?.Invoke($"Сборка ZIP: {buildPath} · сжатие: {DescribeCompression(compression)}");

        var skippedFiles = 0;   // нечитаемые/занятые файлы пропускаем, не валя весь бэкап
        using (var zip = ZipFile.Open(buildPath, ZipArchiveMode.Create))
        {
            foreach (var module in modules)
            {
                ct.ThrowIfCancellationRequested();

                progress?.Report($"{module.DisplayName}: собираю файлы…");

                // Изоляция сбоев: упавший модуль не должен ронять весь бэкап.
                IReadOnlyList<PathEntry> entries;
                try
                {
                    entries = await module.DiscoverAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[eBackup] модуль '{module.Id}' пропущен (ошибка обнаружения): {ex.Message}");
                    log?.Invoke($"✕ Модуль «{module.Id}» пропущен (ошибка обнаружения): {ex.Message}");
                    continue;
                }

                log?.Invoke($"Модуль «{module.DisplayName}» ({module.Id}): {entries.Count} записей для сбора");
                var moduleWatch = Stopwatch.StartNew();
                long moduleBytes = 0;

                var moduleEntry = new ModuleEntry
                {
                    ModuleId = module.Id,
                    DisplayName = module.DisplayName
                };
                var fileCount = 0;

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var source = PathTokens.Resolve(entry.TokenPath);

                    if (entry.Type == PathEntryType.File && File.Exists(source))
                    {
                        var archiveEntryPath = "data/" + entry.ArchivePath.Replace('\\', '/');
                        try
                        {
                            zip.CreateEntryFromFile(source, archiveEntryPath, compression);
                            var length = new FileInfo(source).Length;
                            moduleBytes += length;
                            fileCount++;
                            var sha = await Sha256OfFileAsync(source, ct).ConfigureAwait(false);
                            log?.Invoke($"  + {archiveEntryPath} ({FormatSize(length)}) · sha256 {sha[..12]}…");
                            moduleEntry.Entries.Add(entry with { Sha256 = sha });
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                        {
                            skippedFiles++;
                            log?.Invoke($"  ✕ пропущен (нет доступа/занят): {source} — {ex.Message}");
                        }
                    }
                    else if (entry.Type == PathEntryType.Directory && Directory.Exists(source))
                    {
                        var basePrefix = "data/" + entry.ArchivePath.Replace('\\', '/');

                        // Обобщённые исключения: включаем всё, кроме заданных модулем масок.
                        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                        matcher.AddInclude("**/*");
                        foreach (var glob in entry.ExcludeGlobs)
                            matcher.AddExclude(glob);

                        log?.Invoke($"  Папка {entry.TokenPath} → {source}"
                            + (entry.ExcludeGlobs.Count > 0 ? $" · масок-исключений: {entry.ExcludeGlobs.Count}" : ""));

                        var dirFiles = 0;
                        foreach (var file in matcher.GetResultsInFullPath(source))
                        {
                            ct.ThrowIfCancellationRequested();
                            var rel = Path.GetRelativePath(source, file).Replace('\\', '/');
                            try
                            {
                                zip.CreateEntryFromFile(file, basePrefix + "/" + rel, compression);
                                long length = 0;
                                try { length = new FileInfo(file).Length; } catch { }
                                moduleBytes += length;
                                dirFiles++;
                                log?.Invoke($"  + {basePrefix}/{rel} ({FormatSize(length)})");
                                if (++fileCount % 250 == 0)
                                    progress?.Report($"{module.DisplayName}: {fileCount} файлов…");
                            }
                            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                            {
                                skippedFiles++;
                                log?.Invoke($"  ✕ пропущен (нет доступа/занят): {file} — {ex.Message}");
                            }
                        }
                        log?.Invoke($"  Папка {entry.TokenPath}: {dirFiles} файлов");
                        moduleEntry.Entries.Add(entry);
                    }
                    else
                    {
                        // TODO(v1+): RegistryKey — экспорт/импорт ветки реестра.
                        log?.Invoke($"  – Пропуск: {entry.TokenPath} ({entry.Type switch
                        {
                            PathEntryType.File => "файла нет",
                            PathEntryType.Directory => "папки нет",
                            _ => "тип пока не поддерживается"
                        }})");
                    }
                }

                log?.Invoke($"Модуль «{module.DisplayName}»: итого {fileCount} файлов · "
                    + $"{FormatSize(moduleBytes)} · {moduleWatch.ElapsedMilliseconds} мс");
                manifest.Modules.Add(moduleEntry);
            }

            // Манифест в корень архива.
            progress?.Report("Записываю манифест…");
            log?.Invoke($"Манифест: {manifest.Modules.Count} модулей, "
                + $"{manifest.Modules.Sum(m => m.Entries.Count)} записей");
            var manifestEntry = zip.CreateEntry("manifest.json");
            using var ms = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(ms, manifest, ManifestJson.Options, ct).ConfigureAwait(false);
        }

        log?.Invoke($"ZIP готов: {FormatSize(new FileInfo(buildPath).Length)}");
        if (skippedFiles > 0)
            log?.Invoke($"⚠ Пропущено файлов (нет доступа/заняты): {skippedFiles} — не вошли в архив.");

        // Верификация до того, как архив уйдёт из временной папки: битые данные
        // не должны добраться ни до одного хранилища.
        progress?.Report("Проверяю архив…");
        await VerifyArchiveAsync(buildPath, manifest, log, ct).ConfigureAwait(false);

        if (passphrase is not null)
        {
            progress?.Report("Шифрую архив (AES-256-GCM)…");
            var encryptWatch = Stopwatch.StartNew();
            await ArchiveCipher.EncryptAsync(buildPath, archivePath, passphrase, ct).ConfigureAwait(false);
            File.Delete(buildPath);
            log?.Invoke($"Зашифровано (Argon2id + AES-256-GCM) за {encryptWatch.ElapsedMilliseconds} мс → "
                + FormatSize(new FileInfo(archivePath).Length));
        }

        return archivePath;
    }

    /// <summary>
    /// Полная проверка свежесобранного ZIP: каждая запись распаковывается до конца
    /// (это проверяет её CRC32), а одиночные файлы дополнительно сверяются по SHA-256
    /// с манифестом. Любой брак — исключение, бэкап считается неудавшимся.
    /// </summary>
    private static async Task VerifyArchiveAsync(
        string zipPath, Manifest manifest, Action<string>? log, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var expected = manifest.Modules
            .SelectMany(m => m.Entries)
            .Where(e => e.Sha256 is not null)
            .ToDictionary(
                e => "data/" + e.ArchivePath.Replace('\\', '/'),
                e => e.Sha256!,
                StringComparer.OrdinalIgnoreCase);

        var entriesCount = 0;
        long bytes = 0;
        var hashesChecked = 0;

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = entry.Open();
            if (expected.TryGetValue(entry.FullName, out var sha))
            {
                using var sha256 = SHA256.Create();
                var actual = Convert.ToHexString(
                    await sha256.ComputeHashAsync(stream, ct).ConfigureAwait(false));
                if (!actual.Equals(sha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(
                        $"Верификация провалена: {entry.FullName} — SHA-256 не совпал с манифестом.");
                hashesChecked++;
            }
            else
            {
                await stream.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
            }
            entriesCount++;
            bytes += entry.Length;
        }

        log?.Invoke($"Верификация ✓: {entriesCount} записей · {FormatSize(bytes)} распаковано (CRC32 ок) · "
            + $"SHA-256 сверено: {hashesChecked} · {watch.ElapsedMilliseconds} мс");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1024.0 / 1024 / 1024:0.##} ГБ",
        >= 1L << 20 => $"{bytes / 1024.0 / 1024:0.##} МБ",
        _ => $"{Math.Max(1, bytes / 1024)} КБ"
    };

    private static string DescribeCompression(CompressionLevel level) => level switch
    {
        CompressionLevel.Fastest => "быстрое",
        CompressionLevel.SmallestSize => "максимальное",
        CompressionLevel.NoCompression => "без сжатия",
        _ => "обычное"
    };

    /// <summary>
    /// Распаковать архив .ebk и разложить файлы по местам согласно манифесту.
    /// </summary>
    /// <param name="modules">
    /// Зарегистрированные модули — нужны, чтобы после распаковки вызвать их restore-хуки
    /// (<see cref="IModuleRestoreHook"/>), напр. для размещения ассетов OBS.
    /// </param>
    /// <param name="destinationRootOverride">
    /// Если задано — файлы извлекаются под эту папку (с сохранением структуры
    /// <c>archivePath</c>), вместо разворачивания токенов в реальные системные пути.
    /// Удобно для безопасной проверки восстановления, не затрагивая живые приложения.
    /// </param>
    /// <param name="assetsDirectory">Папка для ассетов, управляемых модулями (если применимо).</param>
    /// <param name="passphrase">Парольная фраза, если архив зашифрован.</param>
    /// <param name="progress">Необязательный прогресс (фазы восстановления).</param>
    /// <param name="entryFilter">
    /// Выборочное восстановление: предикат по полному имени записи ZIP («data/…»).
    /// Если задан — извлекаются только совпавшие файлы; записи, управляемые модулями
    /// (ассеты), извлекаются как обычные файлы по своим исходным путям, а
    /// restore-хуки модулей НЕ вызываются (пост-обработка — только при полном восстановлении).
    /// </param>
    public async Task RestoreAsync(
        string archivePath,
        IEnumerable<IBackupModule>? modules = null,
        ConflictPolicy conflictPolicy = ConflictPolicy.BackupExisting,
        string? destinationRootOverride = null,
        string? assetsDirectory = null,
        string? passphrase = null,
        IProgress<string>? progress = null,
        Action<string>? log = null,
        Func<string, bool>? entryFilter = null,
        CancellationToken ct = default)
    {
        // Зашифрованный архив сначала расшифровываем во временный файл.
        var workingPath = archivePath;
        string? tempPlain = null;
        if (ArchiveCipher.IsEncrypted(archivePath))
        {
            if (string.IsNullOrEmpty(passphrase))
                throw new InvalidOperationException("Архив зашифрован — требуется парольная фраза.");
            progress?.Report("Расшифровываю архив…");
            var decryptWatch = Stopwatch.StartNew();
            tempPlain = Path.Combine(Path.GetTempPath(), $"ebk-dec-{Guid.NewGuid():N}.ebk");
            await ArchiveCipher.DecryptAsync(archivePath, tempPlain, passphrase, ct).ConfigureAwait(false);
            workingPath = tempPlain;
            log?.Invoke($"Расшифрован (Argon2id + AES-256-GCM) за {decryptWatch.ElapsedMilliseconds} мс");
        }

        try
        {
            using var zip = ZipFile.OpenRead(workingPath);
            await RestoreFromArchiveAsync(zip, modules, conflictPolicy, destinationRootOverride,
                assetsDirectory, progress, log, entryFilter, ct).ConfigureAwait(false);
        }
        finally
        {
            if (tempPlain is not null && File.Exists(tempPlain))
                File.Delete(tempPlain);
        }
    }

    /// <summary>
    /// Восстановление из уже открытого ZIP-потока с произвольным доступом — например,
    /// удалённого архива, который читается кусками без скачивания целиком. Поток
    /// должен быть НЕзашифрованным ZIP; временем жизни потока управляет вызывающий.
    /// </summary>
    public async Task RestoreAsync(
        Stream zipStream,
        IEnumerable<IBackupModule>? modules = null,
        ConflictPolicy conflictPolicy = ConflictPolicy.BackupExisting,
        string? destinationRootOverride = null,
        string? assetsDirectory = null,
        IProgress<string>? progress = null,
        Action<string>? log = null,
        Func<string, bool>? entryFilter = null,
        CancellationToken ct = default)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        await RestoreFromArchiveAsync(zip, modules, conflictPolicy, destinationRootOverride,
            assetsDirectory, progress, log, entryFilter, ct).ConfigureAwait(false);
    }

    private async Task RestoreFromArchiveAsync(
        ZipArchive zip,
        IEnumerable<IBackupModule>? modules,
        ConflictPolicy conflictPolicy,
        string? destinationRootOverride,
        string? assetsDirectory,
        IProgress<string>? progress,
        Action<string>? log,
        Func<string, bool>? entryFilter,
        CancellationToken ct)
    {
        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidDataException("В архиве нет manifest.json — это не архив eBackup.");

        Manifest manifest;
        using (var ms = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<Manifest>(ms, ManifestJson.Options, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("Не удалось прочитать manifest.json.");
        }

        // Граница доверия — сам архив: валидируем структуру манифеста (защита от zip-slip / порчи).
        foreach (var m in manifest.Modules)
        {
            if (!ModuleValidation.IsValidId(m.ModuleId))
                throw new InvalidDataException($"Недопустимый ModuleId в манифесте: '{m.ModuleId}'.");
            foreach (var e in m.Entries)
                if (!ModuleValidation.IsSafeArchivePath(e.ArchivePath))
                    throw new InvalidDataException($"Небезопасный archivePath в манифесте: '{e.ArchivePath}'.");
        }

        log?.Invoke($"Манифест: {manifest.Modules.Count} модулей, "
            + $"{manifest.Modules.Sum(m => m.Entries.Count)} записей · создан {manifest.CreatedAt:dd.MM.yyyy HH:mm}"
            + (manifest.Source is { } src ? $" на {src.MachineName}" : ""));
        log?.Invoke(destinationRootOverride is null
            ? "Назначение: исходные пути (режим конфликтов: " + conflictPolicy + ")"
            : $"Назначение: {destinationRootOverride}");

        // Выборочный режим: сбой одного файла (например, ассет с исходным путём
        // на диске, которого на этой машине нет) не должен ронять остальные —
        // копим и отчитываемся в конце. Полное восстановление падает как раньше.
        var selectiveFailures = entryFilter is null ? null : new List<string>();

        foreach (var module in manifest.Modules)
        {
            progress?.Report($"Восстанавливаю: {module.DisplayName}…");
            log?.Invoke($"Модуль «{module.DisplayName}» ({module.ModuleId}): {module.Entries.Count} записей");
            foreach (var entry in module.Entries)
            {
                ct.ThrowIfCancellationRequested();

                // Управляемые модулем записи (ассеты OBS) раскладывает сам модуль через
                // restore-хук. Движок пишет их ТОЛЬКО при извлечении в выбранную папку
                // (путь — строго внутри неё). В исходные места и при полном восстановлении
                // их пропускаем: сырой путь из манифеста как цель записи небезопасен — это
                // работа хука. Иначе выборочное восстановление «в исходные места» писало бы
                // ассет по произвольному пути из (недоверенного) манифеста.
                if (entry.ManagedByModule && (entryFilter is null || destinationRootOverride is null))
                    continue;

                // Путь из манифеста — граница доверия: побеги через «..» запрещены всегда.
                if (PathTokens.HasTraversal(entry.TokenPath))
                {
                    var msg = $"небезопасный путь в манифесте (обход каталога): {entry.TokenPath}";
                    if (selectiveFailures is not null)
                    {
                        selectiveFailures.Add($"{entry.ArchivePath}: {msg}");
                        log?.Invoke($"  ✕ {msg}");
                        continue;
                    }
                    throw new InvalidDataException(msg);
                }

                // В исходные места — по токену/исходному пути; «в папку» — строго внутри неё.
                var target = destinationRootOverride is null
                    ? PathTokens.Resolve(entry.TokenPath)
                    : Path.Combine(destinationRootOverride,
                        entry.ArchivePath.Replace('/', Path.DirectorySeparatorChar));
                if (destinationRootOverride is not null
                    && !PathSafety.IsWithin(destinationRootOverride, target))
                    throw new InvalidDataException(
                        $"Запись выходит за пределы целевой папки: {entry.ArchivePath}");

                var prefix = "data/" + entry.ArchivePath.Replace('\\', '/');

                if (entry.Type == PathEntryType.File)
                {
                    var ze = zip.GetEntry(prefix);
                    if (ze is not null && (entryFilter is null || entryFilter(ze.FullName)))
                        ExtractTolerant(ze, target, conflictPolicy, selectiveFailures, log);
                }
                else if (entry.Type == PathEntryType.Directory)
                {
                    foreach (var ze in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(ze.Name)) continue; // пропустить маркеры папок
                        if (!ze.FullName.StartsWith(prefix + "/", StringComparison.Ordinal)) continue;
                        if (entryFilter is not null && !entryFilter(ze.FullName)) continue;

                        var rel = ze.FullName[(prefix.Length + 1)..];
                        var dest = Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar));
                        if (!PathSafety.IsWithin(target, dest))
                            throw new InvalidDataException($"Запись выходит за пределы целевой папки (zip-slip): {ze.FullName}");
                        ExtractTolerant(ze, dest, conflictPolicy, selectiveFailures, log);
                    }
                }
                // TODO(v1+): RegistryKey — импорт ветки реестра.
            }
        }

        if (selectiveFailures is { Count: > 0 })
            throw new IOException(
                $"Восстановлено не всё: пропущено файлов — {selectiveFailures.Count}. "
                + string.Join("; ", selectiveFailures.Take(3))
                + (selectiveFailures.Count > 3 ? " …" : ""));

        // Модульные restore-хуки: размещение ассетов и пост-обработка.
        // Вся специфика приложения — внутри модуля; движок лишь зовёт хук.
        // При выборочном восстановлении хуки не зовутся (см. entryFilter).
        if (modules is not null && entryFilter is null)
        {
            var hooks = modules.Where(m => m is IModuleRestoreHook).ToDictionary(m => m.Id);
            if (hooks.Count > 0)
            {
                var assetsDir = assetsDirectory ?? DefaultAssetsDirectory();
                foreach (var moduleEntry in manifest.Modules)
                {
                    if (!hooks.TryGetValue(moduleEntry.ModuleId, out var module))
                        continue;

                    progress?.Report($"{moduleEntry.DisplayName}: раскладываю ассеты…");
                    log?.Invoke($"Restore-хук модуля «{moduleEntry.DisplayName}»: раскладываю ассеты…");

                    // Заужаем доступ хука до записей только этого модуля (data/<id>/).
                    var modulePrefix = "data/" + moduleEntry.ModuleId + "/";
                    var modulePaths = zip.Entries
                        .Where(e => !string.IsNullOrEmpty(e.Name) &&
                                    e.FullName.StartsWith(modulePrefix, StringComparison.Ordinal))
                        .Select(e => e.FullName["data/".Length..])
                        .ToList();

                    try
                    {
                    await ((IModuleRestoreHook)module).RestoreAsync(new ModuleRestoreContext
                    {
                        OpenModuleEntry = archivePath =>
                        {
                            var full = "data/" + archivePath.Replace('\\', '/');
                            return full.StartsWith(modulePrefix, StringComparison.Ordinal)
                                ? zip.GetEntry(full)?.Open()
                                : null;
                        },
                        ModuleEntryPaths = modulePaths,
                        ModuleEntry = moduleEntry,
                        AssetsDirectory = assetsDir,
                        DestinationRootOverride = destinationRootOverride
                    }, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[eBackup] restore-хук модуля '{moduleEntry.ModuleId}' завершился с ошибкой: {ex.Message}");
                    }
                }
            }
        }
    }

    private static string DefaultAssetsDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "eBackup", "Assets");

    /// <summary>В выборочном режиме (failures != null) сбой файла копится, не роняя остальное.</summary>
    private static void ExtractTolerant(
        ZipArchiveEntry entry, string destinationPath, ConflictPolicy policy,
        List<string>? failures, Action<string>? log)
    {
        try
        {
            ExtractFile(entry, destinationPath, policy);
            log?.Invoke($"  → {destinationPath} ({FormatSize(entry.Length)})");
        }
        catch (Exception ex) when (failures is not null)
        {
            failures.Add($"{entry.FullName}: {ex.Message}");
            log?.Invoke($"  ✕ {entry.FullName}: {ex.Message}");
        }
    }

    private static void ExtractFile(ZipArchiveEntry entry, string destinationPath, ConflictPolicy policy)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(destinationPath))
        {
            switch (policy)
            {
                case ConflictPolicy.Skip:
                    return;
                case ConflictPolicy.BackupExisting:
                    File.Move(destinationPath, destinationPath + ".bak", overwrite: true);
                    break;
                case ConflictPolicy.Overwrite:
                default:
                    break;
            }
        }

        entry.ExtractToFile(destinationPath, overwrite: true);
    }

    private static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
