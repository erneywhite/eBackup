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
/// Опциональное шифрование (AES-256-GCM поверх готового ZIP) — TODO следующего этапа.
/// </summary>
public sealed class BackupEngine
{
    /// <summary>
    /// Создать архив из набора модулей. Возвращает путь к готовому .ebk.
    /// </summary>
    public async Task<string> CreateBackupAsync(
        IEnumerable<IBackupModule> modules,
        string outputDirectory,
        string archiveName,
        string? passphrase = null,
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

        using (var zip = ZipFile.Open(buildPath, ZipArchiveMode.Create))
        {
            foreach (var module in modules)
            {
                ct.ThrowIfCancellationRequested();

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
                    continue;
                }

                var moduleEntry = new ModuleEntry
                {
                    ModuleId = module.Id,
                    DisplayName = module.DisplayName
                };

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var source = PathTokens.Resolve(entry.TokenPath);

                    if (entry.Type == PathEntryType.File && File.Exists(source))
                    {
                        var archiveEntryPath = "data/" + entry.ArchivePath.Replace('\\', '/');
                        zip.CreateEntryFromFile(source, archiveEntryPath);
                        moduleEntry.Entries.Add(entry with { Sha256 = await Sha256OfFileAsync(source, ct).ConfigureAwait(false) });
                    }
                    else if (entry.Type == PathEntryType.Directory && Directory.Exists(source))
                    {
                        var basePrefix = "data/" + entry.ArchivePath.Replace('\\', '/');

                        // Обобщённые исключения: включаем всё, кроме заданных модулем масок.
                        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                        matcher.AddInclude("**/*");
                        foreach (var glob in entry.ExcludeGlobs)
                            matcher.AddExclude(glob);

                        foreach (var file in matcher.GetResultsInFullPath(source))
                        {
                            ct.ThrowIfCancellationRequested();
                            var rel = Path.GetRelativePath(source, file).Replace('\\', '/');
                            zip.CreateEntryFromFile(file, basePrefix + "/" + rel);
                        }
                        moduleEntry.Entries.Add(entry);
                    }
                    // TODO(v1+): RegistryKey — экспорт/импорт ветки реестра.
                }

                manifest.Modules.Add(moduleEntry);
            }

            // Манифест в корень архива.
            var manifestEntry = zip.CreateEntry("manifest.json");
            using var ms = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(ms, manifest, ManifestJson.Options, ct).ConfigureAwait(false);
        }

        if (passphrase is not null)
        {
            await ArchiveCipher.EncryptAsync(buildPath, archivePath, passphrase, ct).ConfigureAwait(false);
            File.Delete(buildPath);
        }

        return archivePath;
    }

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
    public async Task RestoreAsync(
        string archivePath,
        IEnumerable<IBackupModule>? modules = null,
        ConflictPolicy conflictPolicy = ConflictPolicy.BackupExisting,
        string? destinationRootOverride = null,
        string? assetsDirectory = null,
        string? passphrase = null,
        CancellationToken ct = default)
    {
        // Зашифрованный архив сначала расшифровываем во временный файл.
        var workingPath = archivePath;
        string? tempPlain = null;
        if (ArchiveCipher.IsEncrypted(archivePath))
        {
            if (string.IsNullOrEmpty(passphrase))
                throw new InvalidOperationException("Архив зашифрован — требуется парольная фраза.");
            tempPlain = Path.Combine(Path.GetTempPath(), $"ebk-dec-{Guid.NewGuid():N}.ebk");
            await ArchiveCipher.DecryptAsync(archivePath, tempPlain, passphrase, ct).ConfigureAwait(false);
            workingPath = tempPlain;
        }

        try
        {
        using var zip = ZipFile.OpenRead(workingPath);
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

        foreach (var module in manifest.Modules)
        {
            foreach (var entry in module.Entries)
            {
                ct.ThrowIfCancellationRequested();

                // Управляемые модулем записи (напр. ассеты OBS) восстанавливает
                // сам модуль через свой restore-хук — движок их пропускает.
                if (entry.ManagedByModule)
                    continue;

                var target = destinationRootOverride is null
                    ? PathTokens.Resolve(entry.TokenPath)
                    : Path.Combine(destinationRootOverride, entry.ArchivePath.Replace('/', Path.DirectorySeparatorChar));
                var prefix = "data/" + entry.ArchivePath.Replace('\\', '/');

                if (entry.Type == PathEntryType.File)
                {
                    var ze = zip.GetEntry(prefix);
                    if (ze is not null)
                        ExtractFile(ze, target, conflictPolicy);
                }
                else if (entry.Type == PathEntryType.Directory)
                {
                    foreach (var ze in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(ze.Name)) continue; // пропустить маркеры папок
                        if (!ze.FullName.StartsWith(prefix + "/", StringComparison.Ordinal)) continue;

                        var rel = ze.FullName[(prefix.Length + 1)..];
                        var dest = Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar));
                        if (!PathSafety.IsWithin(target, dest))
                            throw new InvalidDataException($"Запись выходит за пределы целевой папки (zip-slip): {ze.FullName}");
                        ExtractFile(ze, dest, conflictPolicy);
                    }
                }
                // TODO(v1+): RegistryKey — импорт ветки реестра.
            }
        }

        // Модульные restore-хуки: размещение ассетов и пост-обработка.
        // Вся специфика приложения — внутри модуля; движок лишь зовёт хук.
        if (modules is not null)
        {
            var hooks = modules.Where(m => m is IModuleRestoreHook).ToDictionary(m => m.Id);
            if (hooks.Count > 0)
            {
                var assetsDir = assetsDirectory ?? DefaultAssetsDirectory();
                foreach (var moduleEntry in manifest.Modules)
                {
                    if (!hooks.TryGetValue(moduleEntry.ModuleId, out var module))
                        continue;

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
        finally
        {
            if (tempPlain is not null && File.Exists(tempPlain))
                File.Delete(tempPlain);
        }
    }

    private static string DefaultAssetsDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "eBackup", "Assets");

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
