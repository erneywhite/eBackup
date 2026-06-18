using eBackup.Core.Modules;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using eBackup.Service.Jobs;
using eBackup.Storage;

namespace eBackup.Service.Handlers;

/// <summary>
/// Серверная реализация контракта: задачи → <see cref="JobManager"/>, история → <see cref="HistoryStore"/>,
/// модули → <see cref="ModuleRegistry"/>. Администрирование хранилищ/расписаний и стриминг прогресса
/// (AttachToJob) пока заглушены — придут на S4d/S4e/S6. Все решения по доступу — по OwnerSid из caller.
/// </summary>
public sealed class ServiceHandlers : IIpcHandlers
{
    private readonly JobManager _jobs;
    private readonly Core.History.HistoryStore _history;
    private readonly ModuleRegistry _registry;
    private readonly StorageStore _storages;   // машинный конфиг хранилищ (машинный ключ, ProgramData)
    private readonly CustomFolderStore _folders; // реестр «своих папок» (ProgramData)
    private readonly string _instanceId;
    private readonly string _build;

    public ServiceHandlers(JobManager jobs, Core.History.HistoryStore history, ModuleRegistry registry,
        StorageStore storages, string instanceId, string build, CustomFolderStore? folders = null)
    {
        _jobs = jobs;
        _history = history;
        _registry = registry;
        _storages = storages;
        _folders = folders ?? new CustomFolderStore();
        _instanceId = instanceId;
        _build = build;
    }

    public Task<HelloResponse> HelloAsync(HelloRequest req, CallerContext caller, CancellationToken ct)
        => Task.FromResult(new HelloResponse
        {
            ServerProtocol = IpcContractInfo.WireProtocol,
            MinClientProtocol = IpcContractInfo.MinClientProtocol,
            ServerBuild = _build,
            ServiceInstanceId = _instanceId,
            Capabilities = [], // machine-key-secrets / scheduling.serviceOwned добавятся на S6/S7
            UserResolved = true,
        });

    // ---- задачи ----

    public Task<StartBackupResponse> StartBackupAsync(StartBackupRequest req, CallerContext caller, CancellationToken ct)
    {
        var job = _jobs.Enqueue(req, caller.OwnerSid);
        return Task.FromResult(new StartBackupResponse { JobId = job.JobId, RunId = job.RunId, Position = _jobs.Position(job) });
    }

    public Task<Ack> CancelJobAsync(CancelJobRequest req, CallerContext caller, CancellationToken ct)
    {
        if (!_jobs.Cancel(req.JobId, caller.OwnerSid, caller.IsAdmin))
            throw new IpcFaultException(IpcErrorCodes.NotFound, "Задача не найдена или нет прав на отмену.");
        return Task.FromResult(new Ack());
    }

    public Task<JobStatus> GetJobAsync(GetJobRequest req, CallerContext caller, CancellationToken ct)
    {
        var job = _jobs.Get(req.JobId);
        if (job is null || (!caller.IsAdmin && job.OwnerSid != caller.OwnerSid))
            throw new IpcFaultException(IpcErrorCodes.NotFound, "Задача не найдена.");
        return Task.FromResult(JobMapping.ToStatus(job));
    }

    public Task<JobStatus[]> ListJobsAsync(ListJobsRequest req, CallerContext caller, CancellationToken ct)
        => Task.FromResult(_jobs.List(caller.OwnerSid, caller.IsAdmin, req.IncludeFinished)
            .Select(JobMapping.ToStatus).ToArray());

    public Task<StartBackupResponse> RunScheduleNowAsync(RunScheduleNowRequest req, CallerContext caller, CancellationToken ct)
        => throw new IpcFaultException(IpcErrorCodes.Unsupported, "Расписания исполняются службой начиная с S6.");

    public Task<StashPassphraseResponse> StashPassphraseAsync(StashPassphraseRequest req, CallerContext caller, CancellationToken ct)
        => throw new IpcFaultException(IpcErrorCodes.Unsupported, "Шифрование разовой фразой через службу подключим позже.");

    // ---- история ----

    public async Task<BackupRunRecordDto[]> ListHistoryAsync(ListHistoryRequest req, CallerContext caller, CancellationToken ct)
    {
        var all = await _history.LoadAsync().ConfigureAwait(false);
        return all
            .Where(r => caller.IsAdmin || r.OwnerSid is null || r.OwnerSid == caller.OwnerSid)
            .Take(Math.Clamp(req.Limit, 1, HistoryStoreMax))
            .Select(JobMapping.ToDto)
            .ToArray();
    }

    public Task<GetRunLogResponse> GetRunLogAsync(GetRunLogRequest req, CallerContext caller, CancellationToken ct)
    {
        var lines = _history.ReadLogFromSeq(req.RunId, req.FromSeq, Math.Clamp(req.MaxLines, 1, 5000));
        var dto = lines.Select(l => new LogLine { Seq = l.Seq, Text = l.Text }).ToArray();
        var next = dto.Length > 0 ? dto[^1].Seq : req.FromSeq;
        return Task.FromResult(new GetRunLogResponse { Lines = dto, NextSeq = next, HasMore = dto.Length == Math.Clamp(req.MaxLines, 1, 5000) });
    }

    // ---- модули ----

    public Task<ModuleSummary[]> ListModulesAsync(CallerContext caller, CancellationToken ct)
        => Task.FromResult(_registry.Discover()
            .Select(d => new ModuleSummary
            {
                Id = d.Id,
                DisplayName = d.DisplayName,
                Source = d.Source.ToString(),
                Enabled = d.Enabled,
                Problem = d.Problem,
            }).ToArray());

    public Task<Ack> SetModuleEnabledAsync(SetModuleEnabledRequest req, CallerContext caller, CancellationToken ct)
    {
        _registry.SetEnabled(req.Id, req.Enabled);
        return Task.FromResult(new Ack());
    }

    public Task<Ack> InstallModuleAsync(InstallModuleRequest req, CallerContext caller, CancellationToken ct)
        => throw new IpcFaultException(IpcErrorCodes.Unsupported, "Установка модулей через службу (admin-only) — позже.");

    // ---- «свои папки» (реестр в ProgramData; бэкап включает только зарегистрированные) ----

    public Task<string[]> ListCustomFoldersAsync(CallerContext caller, CancellationToken ct)
        => Task.FromResult(_folders.List().ToArray());

    public Task<Ack> UpsertCustomFolderAsync(UpsertCustomFolderRequest req, CallerContext caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Path))
            throw new IpcFaultException(IpcErrorCodes.BadRequest, "Пустой путь папки.");
        _folders.Upsert(req.Path);
        return Task.FromResult(new Ack());
    }

    public Task<Ack> DeleteCustomFolderAsync(DeleteByIdRequest req, CallerContext caller, CancellationToken ct)
    {
        _folders.Remove(req.Id);
        return Task.FromResult(new Ack());
    }

    // ---- хранилища (машинный конфиг, секреты под машинным ключом) ----

    public async Task<StorageSummary[]> ListStoragesAsync(CallerContext caller, CancellationToken ct)
        => (await _storages.LoadAsync(ct).ConfigureAwait(false)).Select(StorageInputMapper.ToSummary).ToArray();

    public async Task<Ack> UpsertStorageAsync(StorageInput req, CallerContext caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Id))
            throw new IpcFaultException(IpcErrorCodes.BadRequest, "Пустой id хранилища.");

        var list = (await _storages.LoadAsync(ct).ConfigureAwait(false)).ToList();
        list.RemoveAll(s => s.Id == req.Id);
        list.Add(StorageInputMapper.ToSavedStorage(req, _storages)); // открытые секреты → машинный ключ
        await _storages.SaveAllAsync(list, ct).ConfigureAwait(false);
        return new Ack();
    }

    public async Task<Ack> DeleteStorageAsync(DeleteByIdRequest req, CallerContext caller, CancellationToken ct)
    {
        var list = (await _storages.LoadAsync(ct).ConfigureAwait(false)).ToList();
        list.RemoveAll(s => s.Id == req.Id);
        await _storages.SaveAllAsync(list, ct).ConfigureAwait(false);
        return new Ack();
    }

    public Task<TestResult> TestStorageAsync(TestStorageRequest req, CallerContext caller, CancellationToken ct)
        => throw new IpcFaultException(IpcErrorCodes.Unsupported, "Проверка хранилищ через службу подключим на S4e.");

    public Task<ScheduleSummary[]> ListSchedulesAsync(CallerContext caller, CancellationToken ct)
        => Task.FromResult(Array.Empty<ScheduleSummary>());

    public Task<Ack> UpsertScheduleAsync(ScheduleInput req, CallerContext caller, CancellationToken ct)
        => throw new IpcFaultException(IpcErrorCodes.Unsupported, "Управление расписаниями через службу — на S6.");

    public Task<Ack> DeleteScheduleAsync(DeleteByIdRequest req, CallerContext caller, CancellationToken ct)
        => throw new IpcFaultException(IpcErrorCodes.Unsupported, "Управление расписаниями через службу — на S6.");

    private const int HistoryStoreMax = 300;
}
