using eBackup.Ipc.Contracts;

namespace eBackup.Ipc.Server;

/// <summary>
/// Тестовая in-memory реализация <see cref="IIpcHandlers"/> для проверки диспетчера/контракта
/// без живой службы. Возвращает канонические заглушки; пару методов ведёт «по-настоящему»,
/// чтобы покрыть happy-path и ветку ошибки.
/// </summary>
public sealed class InMemoryHandlers : IIpcHandlers
{
    /// <summary>Последний принятый StartBackup — для проверки декода тела.</summary>
    public StartBackupRequest? LastStartBackup { get; private set; }

    public Task<HelloResponse> HelloAsync(HelloRequest req, CallerContext caller, CancellationToken ct)
        => Task.FromResult(new HelloResponse
        {
            ServerProtocol = IpcContractInfo.WireProtocol,
            ServiceInstanceId = "test-instance",
            ServerBuild = "test",
            Capabilities = [],
            UserResolved = true,
        });

    public Task<StartBackupResponse> StartBackupAsync(StartBackupRequest req, CallerContext caller, CancellationToken ct)
    {
        LastStartBackup = req;
        return Task.FromResult(new StartBackupResponse { JobId = "job-1", RunId = "run-1", Position = 0 });
    }

    public Task<JobStatus> GetJobAsync(GetJobRequest req, CallerContext caller, CancellationToken ct)
    {
        if (req.JobId == "missing")
            throw new IpcFaultException(IpcErrorCodes.NotFound, "Задача не найдена.");
        return Task.FromResult(new JobStatus { JobId = req.JobId, RunId = "run-1", State = "Running", OwnerSid = caller.OwnerSid });
    }

    // --- остальное: безопасные заглушки (тестами напрямую не задействованы) ---
    public Task<StartBackupResponse> StartRestoreAsync(StartRestoreRequest req, CallerContext caller, CancellationToken ct)
        => Task.FromResult(new StartBackupResponse { JobId = "job-restore", RunId = "run-restore" });
    public Task<StartBackupResponse> RunScheduleNowAsync(RunScheduleNowRequest req, CallerContext caller, CancellationToken ct)
        => Task.FromResult(new StartBackupResponse { JobId = "job-sched", RunId = "run-sched" });
    public Task<Ack> CancelJobAsync(CancelJobRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(new Ack());
    public Task<JobStatus[]> ListJobsAsync(ListJobsRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(Array.Empty<JobStatus>());
    public Task<StashPassphraseResponse> StashPassphraseAsync(StashPassphraseRequest req, CallerContext caller, CancellationToken ct)
        => Task.FromResult(new StashPassphraseResponse { Ticket = "ticket-1", ExpiresAt = default });
    public Task<StorageSummary[]> ListStoragesAsync(CallerContext caller, CancellationToken ct) => Task.FromResult(Array.Empty<StorageSummary>());
    public Task<StorageDetail[]> ListStorageDetailsAsync(CallerContext caller, CancellationToken ct) => Task.FromResult(Array.Empty<StorageDetail>());
    public Task<StorageDetail> GetStorageAsync(GetStorageRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(new StorageDetail { Id = req.Id });
    public Task<Ack> UpsertStorageAsync(StorageInput req, CallerContext caller, CancellationToken ct) => Task.FromResult(new Ack());
    public Task<Ack> DeleteStorageAsync(DeleteByIdRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(new Ack());
    public Task<TestResult> TestStorageAsync(TestStorageRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(new TestResult { Ok = true, Message = "ok" });
    public Task<RemoteFileDto[]> ListArchivesAsync(ListArchivesRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(Array.Empty<RemoteFileDto>());
    public Task<Ack> DeleteArchiveAsync(DeleteArchiveRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(new Ack());
    public Task<ScheduleSummary[]> ListSchedulesAsync(CallerContext caller, CancellationToken ct) => Task.FromResult(Array.Empty<ScheduleSummary>());
    public Task<Ack> UpsertScheduleAsync(ScheduleInput req, CallerContext caller, CancellationToken ct) => Task.FromResult(new Ack());
    public Task<Ack> DeleteScheduleAsync(DeleteByIdRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(new Ack());
    public Task<ModuleSummary[]> ListModulesAsync(CallerContext caller, CancellationToken ct) => Task.FromResult(Array.Empty<ModuleSummary>());
    public Task<ModuleEntryDto[]> DiscoverModuleAsync(DiscoverModuleRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(Array.Empty<ModuleEntryDto>());
    public Task<Ack> SetModuleEnabledAsync(SetModuleEnabledRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(new Ack());
    public Task<Ack> InstallModuleAsync(InstallModuleRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(new Ack());
    public Task<Ack> DeleteModuleAsync(DeleteByIdRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(new Ack());
    public Task<string[]> ListCustomFoldersAsync(CallerContext caller, CancellationToken ct) => Task.FromResult(Array.Empty<string>());
    public Task<Ack> UpsertCustomFolderAsync(UpsertCustomFolderRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(new Ack());
    public Task<Ack> DeleteCustomFolderAsync(DeleteByIdRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(new Ack());
    public Task<BackupRunRecordDto[]> ListHistoryAsync(ListHistoryRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(Array.Empty<BackupRunRecordDto>());
    public Task<GetRunLogResponse> GetRunLogAsync(GetRunLogRequest req, CallerContext caller, CancellationToken ct) => Task.FromResult(new GetRunLogResponse());
}
