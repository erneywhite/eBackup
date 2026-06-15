using eBackup.Ipc.Contracts;

namespace eBackup.Ipc.Server;

/// <summary>
/// Серверная сторона контракта (req/resp операции) — реализует служба на S4. Стриминговые
/// AttachToJob/DetachFromJob идут не сюда, а через живой цикл соединения (S4). Каждый метод
/// получает <see cref="CallerContext"/> для авторизации и per-OwnerSid резолва.
/// Бросать <see cref="IpcFaultException"/> для типизированных ошибок.
/// </summary>
public interface IIpcHandlers
{
    Task<HelloResponse> HelloAsync(HelloRequest req, CallerContext caller, CancellationToken ct);
    Task<StartBackupResponse> StartBackupAsync(StartBackupRequest req, CallerContext caller, CancellationToken ct);
    Task<StartBackupResponse> RunScheduleNowAsync(RunScheduleNowRequest req, CallerContext caller, CancellationToken ct);
    Task<Ack> CancelJobAsync(CancelJobRequest req, CallerContext caller, CancellationToken ct);
    Task<JobStatus> GetJobAsync(GetJobRequest req, CallerContext caller, CancellationToken ct);
    Task<JobStatus[]> ListJobsAsync(ListJobsRequest req, CallerContext caller, CancellationToken ct);
    Task<StashPassphraseResponse> StashPassphraseAsync(StashPassphraseRequest req, CallerContext caller, CancellationToken ct);

    Task<StorageSummary[]> ListStoragesAsync(CallerContext caller, CancellationToken ct);
    Task<Ack> UpsertStorageAsync(StorageInput req, CallerContext caller, CancellationToken ct);
    Task<Ack> DeleteStorageAsync(DeleteByIdRequest req, CallerContext caller, CancellationToken ct);
    Task<TestResult> TestStorageAsync(TestStorageRequest req, CallerContext caller, CancellationToken ct);

    Task<ScheduleSummary[]> ListSchedulesAsync(CallerContext caller, CancellationToken ct);
    Task<Ack> UpsertScheduleAsync(ScheduleInput req, CallerContext caller, CancellationToken ct);
    Task<Ack> DeleteScheduleAsync(DeleteByIdRequest req, CallerContext caller, CancellationToken ct);

    Task<ModuleSummary[]> ListModulesAsync(CallerContext caller, CancellationToken ct);
    Task<Ack> SetModuleEnabledAsync(SetModuleEnabledRequest req, CallerContext caller, CancellationToken ct);
    Task<Ack> InstallModuleAsync(InstallModuleRequest req, CallerContext caller, CancellationToken ct);

    Task<BackupRunRecordDto[]> ListHistoryAsync(ListHistoryRequest req, CallerContext caller, CancellationToken ct);
    Task<GetRunLogResponse> GetRunLogAsync(GetRunLogRequest req, CallerContext caller, CancellationToken ct);
}
