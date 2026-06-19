using eBackup.Ipc.Contracts;

namespace eBackup.Service.Jobs;

public enum JobState { Queued, Running, Completed, CompletedWithErrors, Failed, Cancelled }

public enum JobKind { Backup, Restore }

/// <summary>Итог выполнения задачи бэкапа.</summary>
public sealed record JobOutcome(bool Success, int SkippedFiles, long SizeBytes, string? ArchiveName, string? Error);

/// <summary>Исполнитель одной задачи: в проде — BackupRunner/RestoreRunner (движок + аплоад), в тестах — фейк.</summary>
public interface IJobRunner
{
    Task<JobOutcome> RunAsync(Job job, CancellationToken ct);
}

/// <summary>Задача бэкапа — первоклассный объект службы, живёт независимо от соединения GUI.</summary>
public sealed class Job
{
    public required long Seq { get; init; }            // порядок постановки (для позиции/сортировки)
    public required string JobId { get; init; }
    public required string RunId { get; init; }
    public required string OwnerSid { get; init; }
    public required string Trigger { get; init; }
    public required string Origin { get; init; }        // Interactive | Scheduled
    public JobKind Kind { get; init; } = JobKind.Backup;
    public StartBackupRequest? Request { get; init; }    // для бэкап-задач
    public StartRestoreRequest? Restore { get; init; }   // для restore-задач

    /// <summary>
    /// Разрешённая (расшифрованная) парольная фраза для шифрования/расшифровки. НИКОГДА не в DTO/логах/истории;
    /// раннер зануляет её в finally (CryptographicOperations.ZeroMemory). null — без шифрования.
    /// </summary>
    public byte[]? ResolvedPassphrase { get; init; }

    /// <summary>
    /// Задача ТРЕБУЕТ шифрования (фраза обязана быть). Fail-closed на уровне типа: раннер падает, если
    /// требуется, а фразы нет — движок с passphrase:null при этом НИКОГДА не вызывается (не отдаём открытый архив).
    /// </summary>
    public bool RequiresEncryption { get; init; }

    /// <summary>Шина прогресса/лога этой задачи (журнал + живые подписчики).</summary>
    public required JobChannel Channel { get; init; }

    public CancellationTokenSource Cts { get; } = new();
    public bool IsCancelling { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public JobOutcome? Outcome { get; set; }

    private readonly object _gate = new();
    private JobState _state = JobState.Queued;

    /// <summary>Состояние под локом — пишет рабочий поток, читают обработчики IPC.</summary>
    public JobState State
    {
        get { lock (_gate) { return _state; } }
        set { lock (_gate) { _state = value; } }
    }
}
