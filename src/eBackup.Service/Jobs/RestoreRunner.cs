using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Storage;

namespace eBackup.Service.Jobs;

// UserProfilePaths — в родительском namespace eBackup.Service.

/// <summary>
/// Исполнитель ВОССТАНОВЛЕНИЯ в службе (под SYSTEM): тянет архив из хранилища во временный файл и
/// разворачивает движком с per-SID записью (профиль вызвавшего). Поэтому Program Files пишется
/// (нужны админ-права), а {APPDATA}/{LOCALAPPDATA} идут в профиль пользователя; restore-хуки (OBS)
/// тоже per-SID. Прогресс/лог — в шину задачи (журнал + живые подписчики).
/// Шифрованные архивы пока не поддержаны (S7): движок даст внятный отказ (нужна парольная фраза).
/// </summary>
public sealed class RestoreRunner : IJobRunner
{
    private readonly StorageStore _storages;
    private readonly Func<IReadOnlyList<IBackupModule>> _resolveRestoreModules; // все рабочие модули (для хуков)
    private readonly string _buildDir;

    public RestoreRunner(
        StorageStore storages,
        Func<IReadOnlyList<IBackupModule>> resolveRestoreModules,
        string? buildDir = null)
    {
        _storages = storages;
        _resolveRestoreModules = resolveRestoreModules;
        _buildDir = buildDir ?? Path.Combine(Path.GetTempPath(), "eBackup", "restore");
    }

    public async Task<JobOutcome> RunAsync(Job job, CancellationToken ct)
    {
        var sink = job.Channel;
        void Log(string m) => sink.Log(m);
        var r = job.Restore!;

        var targetLabel = r.TargetDir ?? "исходные места";
        Log($"Запуск: {job.Trigger}");
        Log($"Восстановление: {r.RemoteName} → {targetLabel} · режим конфликтов: {r.Policy}");

        var saved = (await _storages.LoadAsync(ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == r.SourceStorageId)
            ?? throw new InvalidOperationException("Хранилище-источник не найдено.");
        var storage = StorageFactory.Create(saved, _storages.Protector);

        Directory.CreateDirectory(_buildDir);
        var temp = Path.Combine(_buildDir, $"{Guid.NewGuid():N}-{r.RemoteName}");
        try
        {
            sink.Phase($"Получаю {r.RemoteName} из «{saved.Name}»…", 0);
            await storage.DownloadAsync(r.RemoteName, temp, ct).ConfigureAwait(false);
            Log($"Архив получен: {new FileInfo(temp).Length / 1024.0 / 1024.0:0.#} МБ");

            var policy = Enum.TryParse<ConflictPolicy>(r.Policy, out var p) ? p : ConflictPolicy.BackupExisting;
            var resolveDest = new UserProfilePaths(job.OwnerSid).Resolve; // запись в профиль ВЫЗВАВШЕГО (per-SID)
            var progress = new Progress<string>(s => sink.Phase(s, 0));

            var engine = new BackupEngine();
            await engine.RestoreAsync(
                temp,
                _resolveRestoreModules(),
                policy,
                destinationRootOverride: r.TargetDir,
                assetsDirectory: r.AssetsDir,
                passphrase: null,                 // шифрование через службу — S7
                progress: progress,
                log: Log,
                resolveDestination: resolveDest,
                ct: ct).ConfigureAwait(false);

            var skipped = engine.LastRestoreSkippedCount; // занятые/недоступные файлы → «с ошибками», не провал
            if (skipped > 0)
                Log($"⚠ Пропущено занятых/недоступных файлов: {skipped}. Закрой использующие их программы и повтори при необходимости.");
            Log("Готово.");
            return new JobOutcome(true, skipped, 0, r.RemoteName, null);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* temp */ }
        }
    }
}
