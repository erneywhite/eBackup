using eBackup.Core.History;
using eBackup.Ipc.Contracts;

namespace eBackup.Service.Jobs;

/// <summary>Преобразования задача↔DTO/запись истории — общие для обработчиков и журнала.</summary>
public static class JobMapping
{
    public static JobStatus ToStatus(Job j) => new()
    {
        JobId = j.JobId,
        RunId = j.RunId,
        State = j.State.ToString(),
        Trigger = j.Trigger,
        StartedAt = j.StartedAt ?? default,
        FinishedAt = j.FinishedAt,
        OwnerSid = j.OwnerSid,
        JobOrigin = j.Origin,
        IsCancelling = j.IsCancelling,
        ArchiveName = j.Outcome?.ArchiveName,
        SizeBytes = j.Outcome?.SizeBytes ?? 0,
        SkippedFiles = j.Outcome?.SkippedFiles ?? 0,
        Error = j.Outcome?.Error,
        Modules = j.Request.ModuleIds,
        Targets = j.Request.TargetStorageIds,
        ProgressFraction = Fraction(j.State), // настоящая доля прогресса — на S4d (стриминг)
    };

    public static BackupRunRecord ToRecord(Job j) => new()
    {
        Id = j.RunId,
        StartedAt = j.StartedAt ?? DateTimeOffset.Now,
        FinishedAt = j.FinishedAt,
        Trigger = j.Trigger,
        State = j.State.ToString(),
        OwnerSid = j.OwnerSid,
        Modules = [.. j.Request.ModuleIds],
        Targets = [.. j.Request.TargetStorageIds],
        ArchiveName = j.Outcome?.ArchiveName,
        SizeBytes = j.Outcome?.SizeBytes ?? 0,
        SkippedFiles = j.Outcome?.SkippedFiles ?? 0,
        Success = j.State switch
        {
            JobState.Completed or JobState.CompletedWithErrors => true,
            JobState.Failed or JobState.Cancelled => false,
            _ => null, // Queued/Running — не завершено
        },
        Error = j.Outcome?.Error,
    };

    public static BackupRunRecordDto ToDto(BackupRunRecord r) => new()
    {
        Id = r.Id,
        StartedAt = r.StartedAt,
        FinishedAt = r.FinishedAt,
        Trigger = r.Trigger,
        Success = r.Success,
        State = r.State,
        SizeBytes = r.SizeBytes,
        SkippedFiles = r.SkippedFiles,
        Modules = [.. r.Modules],
        Targets = [.. r.Targets],
        Error = r.Error,
    };

    private static double Fraction(JobState s) => s switch
    {
        JobState.Completed or JobState.CompletedWithErrors or JobState.Failed or JobState.Cancelled => 1.0,
        JobState.Running => 0.5,
        _ => 0.0,
    };
}
