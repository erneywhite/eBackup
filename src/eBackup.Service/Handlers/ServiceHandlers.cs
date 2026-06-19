using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using eBackup.Core.Model;
using eBackup.Core.Modules;
using eBackup.Core.Scheduling;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using eBackup.Platform;
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
    private readonly ScheduleStore _schedules; // машинный конфиг расписаний (машинный ключ, ProgramData)
    private readonly CustomFolderStore _folders; // реестр «своих папок» (ProgramData)
    private readonly PassphraseVault _vault;     // разовые тикеты фраз для интерактивного шифрования
    private readonly string _modulesDir;         // куда InstallModule пишет *.module.json (ProgramData)
    private readonly string _instanceId;
    private readonly string _build;

    public ServiceHandlers(JobManager jobs, Core.History.HistoryStore history, ModuleRegistry registry,
        StorageStore storages, string instanceId, string build,
        CustomFolderStore? folders = null, string? modulesDir = null, ScheduleStore? schedules = null,
        PassphraseVault? vault = null)
    {
        _jobs = jobs;
        _history = history;
        _registry = registry;
        _storages = storages;
        _schedules = schedules ?? new ScheduleStore(storages.Protector, AppPaths.MachineSchedulesFile);
        _folders = folders ?? new CustomFolderStore();
        _vault = vault ?? new PassphraseVault();
        _modulesDir = modulesDir ?? AppPaths.MachineModulesDir;
        _instanceId = instanceId;
        _build = build;
    }

    private const int PassphraseTicketSeconds = 30; // короткое окно Stash→Start (решение пользователя)

    public Task<HelloResponse> HelloAsync(HelloRequest req, CallerContext caller, CancellationToken ct)
        => Task.FromResult(new HelloResponse
        {
            ServerProtocol = IpcContractInfo.WireProtocol,
            MinClientProtocol = IpcContractInfo.MinClientProtocol,
            ServerBuild = _build,
            ServiceInstanceId = _instanceId,
            Capabilities = [IpcContractInfo.Capabilities.SchedulingServiceOwned], // расписания исполняет служба
            UserResolved = true,
        });

    // ---- задачи ----

    public Task<StartBackupResponse> StartBackupAsync(StartBackupRequest req, CallerContext caller, CancellationToken ct)
    {
        var (passphrase, requires) = ResolvePassphrase(req.Passphrase, caller);
        if (requires) req = req with { Passphrase = null }; // тикет израсходован — не оседает в истории
        var job = _jobs.Enqueue(req, caller.OwnerSid, resolvedPassphrase: passphrase, requiresEncryption: requires);
        return Task.FromResult(new StartBackupResponse { JobId = job.JobId, RunId = job.RunId, Position = _jobs.Position(job) });
    }

    public Task<StartBackupResponse> StartRestoreAsync(StartRestoreRequest req, CallerContext caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.SourceStorageId) || string.IsNullOrWhiteSpace(req.RemoteName))
            throw new IpcFaultException(IpcErrorCodes.BadRequest, "Не указан источник восстановления.");
        var (passphrase, requires) = ResolvePassphrase(req.Passphrase, caller);
        if (requires) req = req with { Passphrase = null };
        var job = _jobs.EnqueueRestore(req, caller.OwnerSid, resolvedPassphrase: passphrase, requiresEncryption: requires);
        return Task.FromResult(new StartBackupResponse { JobId = job.JobId, RunId = job.RunId, Position = _jobs.Position(job) });
    }

    /// <summary>
    /// Тикет → расшифрованная фраза (single-use через PassphraseVault). none/null → (null, false).
    /// Ошибки тикета → типизированный fault (fail-closed: без фразы шифровать/расшифровывать нельзя).
    /// </summary>
    private (byte[]? passphrase, bool requiresEncryption) ResolvePassphrase(PassphraseRef? reference, CallerContext caller)
    {
        if (reference is null || reference.Kind != "ticket")
            return (null, false);
        if (string.IsNullOrEmpty(reference.Ticket))
            throw new IpcFaultException(IpcErrorCodes.PassphraseTicketInvalid, "Пустой тикет парольной фразы.");

        var (result, bytes) = _vault.Consume(reference.Ticket, caller.OwnerSid, DateTimeOffset.Now);
        return result switch
        {
            PassphraseVault.TicketResult.Ok => (bytes, true),
            PassphraseVault.TicketResult.Expired =>
                throw new IpcFaultException(IpcErrorCodes.PassphraseTicketExpired, "Срок действия парольной фразы истёк — повторите."),
            _ => throw new IpcFaultException(IpcErrorCodes.PassphraseTicketInvalid, "Тикет парольной фразы недействителен."),
        };
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

    public async Task<StartBackupResponse> RunScheduleNowAsync(RunScheduleNowRequest req, CallerContext caller, CancellationToken ct)
    {
        var s = (await _schedules.LoadAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Id == req.ScheduleId)
            ?? throw new IpcFaultException(IpcErrorCodes.NotFound, "Расписание не найдено.");
        if (!CanManage(s, caller))
            throw new IpcFaultException(IpcErrorCodes.NotOwner, "Это расписание создано другим пользователем.");
        if (s.ProtectedPassphrase is not null)
            throw new IpcFaultException(IpcErrorCodes.Unsupported,
                "Зашифрованные бэкапы по расписанию пока выполняются только вручную (появится в следующем обновлении).");

        // Запускаем под профилем ВЛАДЕЛЬЦА расписания (per-SID резолв источников), помечаем как Scheduled.
        var job = _jobs.Enqueue(ScheduleInputMapper.ToBackupRequest(s), s.OwnerSid ?? caller.OwnerSid, origin: "Scheduled");
        return new StartBackupResponse { JobId = job.JobId, RunId = job.RunId, Position = _jobs.Position(job) };
    }

    public Task<StashPassphraseResponse> StashPassphraseAsync(StashPassphraseRequest req, CallerContext caller, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Plaintext))
            throw new IpcFaultException(IpcErrorCodes.BadRequest, "Пустая парольная фраза.");

        var bytes = Encoding.UTF8.GetBytes(req.Plaintext); // string→byte[]; string-копию занулить нельзя (остаточный риск)
        try
        {
            var ttl = TimeSpan.FromSeconds(PassphraseTicketSeconds);
            var now = DateTimeOffset.Now;
            var ticket = _vault.Issue(bytes, caller.OwnerSid, ttl, now);
            return Task.FromResult(new StashPassphraseResponse { Ticket = ticket, ExpiresAt = now + ttl });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

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

    public async Task<ModuleEntryDto[]> DiscoverModuleAsync(DiscoverModuleRequest req, CallerContext caller, CancellationToken ct)
    {
        var d = _registry.Discover().FirstOrDefault(x => x.Id == req.Id);
        if (d?.Instance is null) return [];
        var entries = await d.Instance.DiscoverAsync(ct).ConfigureAwait(false);
        return entries.Select(e => new ModuleEntryDto { TokenPath = e.TokenPath, Type = e.Type.ToString() }).ToArray();
    }

    public Task<Ack> SetModuleEnabledAsync(SetModuleEnabledRequest req, CallerContext caller, CancellationToken ct)
    {
        _registry.SetEnabled(req.Id, req.Enabled);
        return Task.FromResult(new Ack());
    }

    public Task<Ack> InstallModuleAsync(InstallModuleRequest req, CallerContext caller, CancellationToken ct)
    {
        // Декларативный модуль: GUI присылает уже скачанный JSON (каталог/импорт), служба валидирует
        // и пишет в свою папку модулей (ProgramData). CatalogRef-скачивание службой — не делаем (у GUI сеть).
        if (string.IsNullOrWhiteSpace(req.DeclarativeJson))
            throw new IpcFaultException(IpcErrorCodes.Unsupported, "Поддерживается только установка декларативного модуля (DeclarativeJson).");

        DeclarativeModuleJson? parsed;
        try { parsed = JsonSerializer.Deserialize<DeclarativeModuleJson>(req.DeclarativeJson, ManifestJson.Options); }
        catch { throw new IpcFaultException(IpcErrorCodes.BadRequest, "Не удалось разобрать модуль."); }
        if (parsed is null || !ModuleValidation.IsValidId(parsed.Id))
            throw new IpcFaultException(IpcErrorCodes.BadRequest, "Некорректный id или формат модуля.");

        Directory.CreateDirectory(_modulesDir);
        File.WriteAllText(Path.Combine(_modulesDir, parsed.Id + ".module.json"), req.DeclarativeJson);
        return Task.FromResult(new Ack());
    }

    public Task<Ack> DeleteModuleAsync(DeleteByIdRequest req, CallerContext caller, CancellationToken ct)
    {
        // Удаляем только ДЕКЛАРАТИВНЫЙ дескриптор (встроенные не трогаем) по его файлу-источнику.
        var d = _registry.Discover().FirstOrDefault(
            x => x.Id == req.Id && x.Source == ModuleSource.Declarative && x.Origin is not null);
        if (d?.Origin is not null)
            try { File.Delete(d.Origin); } catch { /* уже нет — не критично */ }
        return Task.FromResult(new Ack());
    }

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

    public async Task<StorageDetail[]> ListStorageDetailsAsync(CallerContext caller, CancellationToken ct)
        => (await _storages.LoadAsync(ct).ConfigureAwait(false)).Select(StorageInputMapper.ToDetail).ToArray();

    public async Task<StorageDetail> GetStorageAsync(GetStorageRequest req, CallerContext caller, CancellationToken ct)
    {
        var s = (await _storages.LoadAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Id == req.Id)
            ?? throw new IpcFaultException(IpcErrorCodes.NotFound, "Хранилище не найдено.");
        return StorageInputMapper.ToDetail(s); // только несекретные поля + имена присутствующих секретов
    }

    public async Task<Ack> UpsertStorageAsync(StorageInput req, CallerContext caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Id))
            throw new IpcFaultException(IpcErrorCodes.BadRequest, "Пустой id хранилища.");

        var list = (await _storages.LoadAsync(ct).ConfigureAwait(false)).ToList();
        var existing = list.FirstOrDefault(s => s.Id == req.Id); // не теряем секрет при правке без его ввода
        list.RemoveAll(s => s.Id == req.Id);
        list.Add(StorageInputMapper.ToSavedStorage(req, _storages, existing)); // открытые секреты → машинный ключ
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

    public async Task<TestResult> TestStorageAsync(TestStorageRequest req, CallerContext caller, CancellationToken ct)
    {
        // Проверка идёт ИЗ СЛУЖБЫ (под SYSTEM, секреты под машинным ключом) — это и есть смысл переезда.
        var list = (await _storages.LoadAsync(ct).ConfigureAwait(false)).ToList();
        SavedStorage saved;
        if (!string.IsNullOrEmpty(req.StorageId))
            saved = list.FirstOrDefault(s => s.Id == req.StorageId)
                ?? throw new IpcFaultException(IpcErrorCodes.NotFound, "Хранилище не найдено.");
        else if (req.Inline is { } inline)
            // тест ещё несохранённого: «оставленные» секреты берём из уже сохранённого с тем же id
            saved = StorageInputMapper.ToSavedStorage(inline, _storages, list.FirstOrDefault(s => s.Id == inline.Id));
        else
            throw new IpcFaultException(IpcErrorCodes.BadRequest, "Не указано хранилище для проверки.");

        var result = await StorageFactory.Create(saved, _storages.Protector).TestAsync(ct).ConfigureAwait(false);
        long? free = null;
        if (saved.Kind == StorageKind.LocalFolder && saved.Path is { } p && TryFreeBytes(p, out var f))
            free = f;
        return new TestResult { Ok = result.Success, Message = result.Message, FreeBytes = free };
    }

    public async Task<RemoteFileDto[]> ListArchivesAsync(ListArchivesRequest req, CallerContext caller, CancellationToken ct)
    {
        // Листинг архивов ИЗ СЛУЖБЫ: у GUI нет секрета хранилища (он под машинным ключом).
        var saved = (await _storages.LoadAsync(ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == req.StorageId)
            ?? throw new IpcFaultException(IpcErrorCodes.NotFound, "Хранилище не найдено.");
        var files = await StorageFactory.Create(saved, _storages.Protector).ListDetailedAsync(ct).ConfigureAwait(false);
        return files
            .Select(f => new RemoteFileDto { Name = f.Name, Length = f.Length, LastWriteTime = f.LastWriteTime })
            .ToArray();
    }

    public async Task<Ack> DeleteArchiveAsync(DeleteArchiveRequest req, CallerContext caller, CancellationToken ct)
    {
        var saved = (await _storages.LoadAsync(ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == req.StorageId)
            ?? throw new IpcFaultException(IpcErrorCodes.NotFound, "Хранилище не найдено.");
        await StorageFactory.Create(saved, _storages.Protector).DeleteAsync(req.RemoteName, ct).ConfigureAwait(false);
        return new Ack();
    }

    private static bool TryFreeBytes(string path, out long free)
    {
        free = 0;
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false; // UNC и т.п. — пропускаем
            free = new System.IO.DriveInfo(root).AvailableFreeSpace;
            return true;
        }
        catch { return false; }
    }

    // ---- расписания (машинный конфиг, фразы под машинным ключом; авторизация по OwnerSid) ----

    /// <summary>Управлять/видеть расписание может админ, владелец или (легаси) бесхозное.</summary>
    private static bool CanManage(BackupSchedule s, CallerContext caller)
        => caller.IsAdmin || s.OwnerSid is null || s.OwnerSid == caller.OwnerSid;

    public async Task<ScheduleDetail[]> ListSchedulesAsync(CallerContext caller, CancellationToken ct)
    {
        var now = DateTime.Now;
        var all = await _schedules.LoadAsync(ct).ConfigureAwait(false);
        return all.Where(s => CanManage(s, caller))
            .Select(s => ScheduleInputMapper.ToDetail(s, now))
            .ToArray();
    }

    public async Task<Ack> UpsertScheduleAsync(ScheduleInput req, CallerContext caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Id))
            throw new IpcFaultException(IpcErrorCodes.BadRequest, "Пустой id расписания.");
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new IpcFaultException(IpcErrorCodes.BadRequest, "Пустое имя расписания.");

        var list = (await _schedules.LoadAsync(ct).ConfigureAwait(false)).ToList();
        var existing = list.FirstOrDefault(s => s.Id == req.Id);
        if (existing is not null && !CanManage(existing, caller))
            throw new IpcFaultException(IpcErrorCodes.NotOwner, "Это расписание создано другим пользователем.");

        var mapped = ScheduleInputMapper.ToSchedule(req, _schedules, existing, caller.OwnerSid);
        if (existing is null)
            mapped = mapped with { LastRunAt = DateTime.Now }; // новое не срабатывает задним числом

        list.RemoveAll(s => s.Id == req.Id);
        list.Add(mapped);
        await _schedules.SaveAllAsync(list, ct).ConfigureAwait(false);
        return new Ack();
    }

    public async Task<Ack> DeleteScheduleAsync(DeleteByIdRequest req, CallerContext caller, CancellationToken ct)
    {
        var list = (await _schedules.LoadAsync(ct).ConfigureAwait(false)).ToList();
        var existing = list.FirstOrDefault(s => s.Id == req.Id);
        if (existing is null)
            return new Ack(); // нечего удалять — идемпотентно
        if (!CanManage(existing, caller))
            throw new IpcFaultException(IpcErrorCodes.NotOwner, "Это расписание создано другим пользователем.");

        list.RemoveAll(s => s.Id == req.Id);
        await _schedules.SaveAllAsync(list, ct).ConfigureAwait(false);
        return new Ack();
    }

    private const int HistoryStoreMax = 300;
}
