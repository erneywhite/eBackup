using System.IO.Compression;
using System.Security.Cryptography;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Localization;
using eBackup.Storage;

namespace eBackup.Service.Jobs;

// UserProfilePaths — в родительском namespace eBackup.Service.

/// <summary>
/// Настоящий исполнитель задачи: движок собирает архив из разрешённых по id модулей и пишет
/// подробный лог в журнал истории (seq-строки). Читает источники как есть (служба под SYSTEM —
/// в этом смысл, граница безопасности на стороне доверенных модулей/папок).
///
/// Сборка .ebk → заливка на выбранные хранилища (по id из машинного конфига) с верификацией и
/// retention. Если хранилища не выбраны — архив остаётся в build-папке (локальный режим).
/// Шифрование: если задан Job.ResolvedPassphrase — архив шифруется (fail-closed, см. ниже).
/// </summary>
public sealed class BackupRunner : IJobRunner
{
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<IBackupModule>> _resolveModules;
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<string>>? _resolveFolders;
    private readonly StorageStore? _storages;   // машинный конфиг хранилищ (null в тестах сборки = локальный режим)
    private readonly string _buildDir;

    public static string DefaultBuildDir => Path.Combine(Path.GetTempPath(), "eBackup", "build");

    public BackupRunner(
        Func<IReadOnlyList<string>, IReadOnlyList<IBackupModule>> resolveModules,
        string? buildDir = null,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? resolveFolders = null,
        StorageStore? storages = null)
    {
        _resolveModules = resolveModules;
        _buildDir = buildDir ?? DefaultBuildDir;
        _resolveFolders = resolveFolders;
        _storages = storages;
    }

    /// <summary>Подмести осиротевшие per-run папки сборки (хвосты от падения/убийства процесса). Зовётся на старте службы.</summary>
    public static void SweepOrphanRuns(string? buildDir = null)
    {
        var dir = buildDir ?? DefaultBuildDir;
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var run in Directory.EnumerateDirectories(dir, "run-*"))
                try { Directory.Delete(run, recursive: true); } catch { /* temp */ }
        }
        catch { /* нет папки/нет доступа — не критично */ }
    }

    public async Task<JobOutcome> RunAsync(Job job, CancellationToken ct)
    {
        var sink = job.Channel;            // прогресс/лог идут в шину задачи (журнал + живые подписчики)
        void Log(string m) => sink.Log(m);
        void WipePassphrase() { if (job.ResolvedPassphrase is { } pw) CryptographicOperations.ZeroMemory(pw); }

        // Fail-closed на уровне типа: шифрование затребовано, но фразы нет → НЕ зовём движок с passphrase:null
        // (иначе отдали бы ОТКРЫТЫЙ архив). Закрывает и потерю слота при постановке, и провал резолва.
        if (job.RequiresEncryption && (job.ResolvedPassphrase is null || job.ResolvedPassphrase.Length == 0))
        {
            Log("Бэкап отменён: затребовано шифрование, но парольная фраза недоступна.");
            return new JobOutcome(false, 0, 0, null, "Шифрование затребовано, но парольная фраза недоступна.");
        }

        var allModules = new List<IBackupModule>(_resolveModules(job.Request!.ModuleIds));
        var folders = _resolveFolders?.Invoke(job.Request!.CustomFolderIds) ?? [];
        if (folders.Count > 0) allModules.Add(new CustomFoldersModule(folders));
        if (allModules.Count == 0)
        {
            WipePassphrase();
            Log("Бэкап отменён: не выбрано ни модулей, ни папок.");
            return new JobOutcome(false, 0, 0, null, "Не выбрано ни модулей, ни папок.");
        }

        // Своя папка на каждый прогон → изоляция и гарантированная зачистка (вся run-папка удаляется в finally).
        var runDir = Path.Combine(_buildDir, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);

        var name = BackupNaming.DefaultName(allModules,
            machineTag: job.Request!.IncludeMachineName ? Environment.MachineName : null);
        var compression = job.Request!.CompressionMode switch
        {
            0 => CompressionLevel.Fastest,
            2 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        Log($"Запуск: {job.Trigger}");
        Log("В бэкапе: " + string.Join(", ", allModules.Select(m => m.Id)));

        var engine = new BackupEngine();
        // Крупные фазы → Phase-ноты (живой прогресс); реальная доля прогресса — позже.
        var progress = new Progress<string>(s => sink.Phase(s, 0));
        // Источники резолвим в профиль ВЫЗВАВШЕГО пользователя (служба под SYSTEM), а не системный.
        var resolveSource = new UserProfilePaths(job.OwnerSid).Resolve;
        // Фраза (если есть) уже разрешена службой в Job.ResolvedPassphrase. На границе движка неизбежна
        // managed-string копия (zeroing невозможен) — принятый остаточный риск; byte[] зануляем в finally.
        var passphrase = job.ResolvedPassphrase is { Length: > 0 } pwBytes
            ? System.Text.Encoding.UTF8.GetString(pwBytes)
            : null;

        try
        {
            var archive = await engine.CreateBackupAsync(
                allModules, runDir, name, passphrase: passphrase,
                progress: progress, compression: compression, log: Log, resolveSource: resolveSource, ct: ct)
                .ConfigureAwait(false);

            var size = new FileInfo(archive).Length;
            var archiveName = Path.GetFileName(archive);
            Log($"Архив собран: {archiveName} — {Mb(size)} МБ, пропущено файлов: {engine.LastSkippedCount}");

            var targetIds = job.Request!.TargetStorageIds;
            if (_storages is null || targetIds.Length == 0)
            {
                // Локальный режим: переносим архив из run-папки в стабильное место (run-папку зачистит finally).
                try { File.Move(archive, Path.Combine(_buildDir, archiveName), overwrite: true); } catch { /* останется в run-папке */ }
                Log("Хранилища не выбраны — архив сохранён локально.");
                return new JobOutcome(true, engine.LastSkippedCount, size, archiveName, null);
            }

            // Заливка на выбранные хранилища (порядок — как у пользователя в запросе) + верификация + retention.
            var all = await _storages.LoadAsync(ct).ConfigureAwait(false);
            var targets = targetIds
                .Select(id => all.FirstOrDefault(s => s.Id == id))
                .Where(s => s is not null).Select(s => s!)
                .ToList();
            var missing = targetIds.Length - targets.Count;
            if (missing > 0) Log($"⚠ Пропущено хранилищ (не найдены в конфиге службы): {missing}");

            var done = new List<string>();
            var failed = new List<string>();
            var retention = job.Request!.RetentionCount ?? 0;
            string? localSha = null;
            var step = 0;

            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                sink.Phase(L.Get("Phase_SavingToTarget", target.Name), 0);
                Log($"«{target.Name}»: заливаю…");
                try
                {
                    // Для локальной папки честно отказываемся ДО заливки, если место не влезает.
                    if (target.Kind == StorageKind.LocalFolder && target.Path is { } tp
                        && TryFreeBytes(tp, out var free) && free < size)
                        throw new IOException($"недостаточно места: нужно {Mb(size)} МБ, свободно {Mb(free)} МБ");

                    var storage = StorageFactory.Create(target, _storages.Protector);
                    await storage.UploadAsync(archive, archiveName, ct).ConfigureAwait(false);

                    // Верификация: файл появился в листинге с верным размером; для папок — ещё сверка SHA-256.
                    var files = await storage.ListDetailedAsync(ct).ConfigureAwait(false);
                    var remote = files.FirstOrDefault(f => f.Name == archiveName);
                    if (remote is null)
                        throw new IOException("верификация: файл не появился в листинге после заливки");
                    if (remote.Length > 0 && remote.Length != size)
                        throw new IOException($"верификация: размер не совпал (локально {size} Б, в хранилище {remote.Length} Б)");

                    if (storage is FolderStorage folder)
                    {
                        localSha ??= await Task.Run(() => Sha256(archive), ct).ConfigureAwait(false);
                        var copySha = await Task.Run(() => Sha256(folder.GetLocalPath(archiveName)), ct).ConfigureAwait(false);
                        if (!copySha.Equals(localSha, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("верификация: SHA-256 копии не совпал с архивом");
                        Log($"«{target.Name}»: верификация ✓ SHA-256 копии совпадает");
                    }
                    else
                    {
                        Log(remote.Length > 0
                            ? $"«{target.Name}»: верификация ✓ размер совпадает ({Mb(remote.Length)} МБ)"
                            : $"«{target.Name}»: верификация — хранилище не сообщает размер, сверка пропущена");
                    }

                    done.Add(target.Name);

                    // Retention по ГРУППАМ (набор модулей закодирован в имени): «последние N» в своей группе.
                    if (retention > 0)
                    {
                        var groupKey = BackupNaming.RetentionGroupKey(archiveName);
                        var sameGroup = files
                            .Where(f => BackupNaming.RetentionGroupKey(f.Name) == groupKey)
                            .OrderByDescending(f => f.LastWriteTime)
                            .ToList();
                        foreach (var old in sameGroup.Skip(retention))
                        {
                            await storage.DeleteAsync(old.Name, ct).ConfigureAwait(false);
                            Log($"«{target.Name}»: retention (группа {groupKey}) — удалил {old.Name}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw; // отмена — не ошибка цели, пробрасываем наверх
                }
                catch (Exception ex)
                {
                    failed.Add($"{target.Name}: {ex.Message}");
                    Log($"«{target.Name}»: ✕ {ex.Message}");
                }
                sink.Phase(L.Get("Phase_Uploading"), (double)(++step) / targets.Count);
            }

            var ok = failed.Count == 0;
            var error = ok ? null : string.Join("; ", failed);
            Log(ok ? $"Готово → {string.Join(", ", done)}" : $"Завершено с ошибками: {error}");
            return new JobOutcome(ok, engine.LastSkippedCount, size, archiveName, error);
        }
        catch (OperationCanceledException)
        {
            Log("⏹ Отменено — временные файлы сборки убраны.");
            throw; // JobManager пометит задачу Cancelled
        }
        finally
        {
            WipePassphrase(); // занулить фразу всегда (успех/отмена/ошибка)
            // Вся run-папка (включая .ebk и любой .plain) удаляется ВСЕГДА — успех/отмена/ошибка.
            try { Directory.Delete(runDir, recursive: true); } catch { /* temp run-папка */ }
        }
    }

    private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.#");

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static bool TryFreeBytes(string path, out long free)
    {
        free = 0;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false; // UNC и т.п. — проверку пропускаем
            free = new DriveInfo(root).AvailableFreeSpace;
            return true;
        }
        catch { return false; }
    }
}
