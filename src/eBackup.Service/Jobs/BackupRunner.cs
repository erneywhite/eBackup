using System.IO.Compression;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;

namespace eBackup.Service.Jobs;

// UserProfilePaths — в родительском namespace eBackup.Service.

/// <summary>
/// Настоящий исполнитель задачи: движок собирает архив из разрешённых по id модулей и пишет
/// подробный лог в журнал истории (seq-строки). Читает источники как есть (служба под SYSTEM —
/// в этом смысл, граница безопасности на стороне доверенных модулей/папок).
///
/// S4c-3a: только ЛОКАЛЬНАЯ сборка .ebk без шифрования. Аплоад/verify/retention на хранилища и
/// шифрование переедут сюда позже — им нужны секреты под машинным ключом (активация на S7).
/// </summary>
public sealed class BackupRunner : IJobRunner
{
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<IBackupModule>> _resolveModules;
    private readonly string _buildDir;

    public BackupRunner(
        Func<IReadOnlyList<string>, IReadOnlyList<IBackupModule>> resolveModules,
        string? buildDir = null)
    {
        _resolveModules = resolveModules;
        _buildDir = buildDir ?? Path.Combine(Path.GetTempPath(), "eBackup", "build");
    }

    public async Task<JobOutcome> RunAsync(Job job, CancellationToken ct)
    {
        var sink = job.Channel;            // прогресс/лог идут в шину задачи (журнал + живые подписчики)
        void Log(string m) => sink.Log(m);

        var modules = _resolveModules(job.Request.ModuleIds);
        if (modules.Count == 0)
        {
            Log("Бэкап отменён: не найдено ни одного модуля по заданным id.");
            return new JobOutcome(false, 0, 0, null, "Не выбрано ни одного модуля.");
        }

        Directory.CreateDirectory(_buildDir);
        var name = BackupNaming.DefaultName(modules,
            machineTag: job.Request.IncludeMachineName ? Environment.MachineName : null);
        var compression = job.Request.CompressionMode switch
        {
            0 => CompressionLevel.Fastest,
            2 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        Log($"Запуск: {job.Trigger}");
        Log("Модули: " + string.Join(", ", modules.Select(m => m.Id)));

        var engine = new BackupEngine();
        // Крупные фазы → Phase-ноты (живой прогресс); реальная доля прогресса — позже.
        var progress = new Progress<string>(s => sink.Phase(s, 0));
        // Источники резолвим в профиль ВЫЗВАВШЕГО пользователя (служба под SYSTEM), а не системный.
        var resolveSource = new UserProfilePaths(job.OwnerSid).Resolve;
        var archive = await engine.CreateBackupAsync(
            modules, _buildDir, name, passphrase: null,
            progress: progress, compression: compression, log: Log, resolveSource: resolveSource, ct: ct)
            .ConfigureAwait(false);

        var size = new FileInfo(archive).Length;
        Log($"Готово: {Path.GetFileName(archive)} — {size} байт, пропущено файлов: {engine.LastSkippedCount}");
        return new JobOutcome(true, engine.LastSkippedCount, size, Path.GetFileName(archive), null);
    }
}
