using System.IO.Compression;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Core.History;

namespace eBackup.Service.Jobs;

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
    private readonly HistoryStore _history;
    private readonly string _buildDir;

    public BackupRunner(
        Func<IReadOnlyList<string>, IReadOnlyList<IBackupModule>> resolveModules,
        HistoryStore history,
        string? buildDir = null)
    {
        _resolveModules = resolveModules;
        _history = history;
        _buildDir = buildDir ?? Path.Combine(Path.GetTempPath(), "eBackup", "build");
    }

    public async Task<JobOutcome> RunAsync(Job job, CancellationToken ct)
    {
        void Log(string m) => _history.AppendLog(job.RunId, m);

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
        var progress = new Progress<string>(Log);
        var archive = await engine.CreateBackupAsync(
            modules, _buildDir, name, passphrase: null, progress, compression, log: Log, ct)
            .ConfigureAwait(false);

        var size = new FileInfo(archive).Length;
        Log($"Готово: {Path.GetFileName(archive)} — {size} байт, пропущено файлов: {engine.LastSkippedCount}");
        return new JobOutcome(true, engine.LastSkippedCount, size, Path.GetFileName(archive), null);
    }
}
