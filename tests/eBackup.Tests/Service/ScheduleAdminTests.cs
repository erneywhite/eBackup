using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.History;
using eBackup.Core.Modules;
using eBackup.Core.Scheduling;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using eBackup.Security;
using eBackup.Service.Handlers;
using eBackup.Service.Jobs;
using eBackup.Storage;
using Xunit;

namespace eBackup.Tests.Service;

public sealed class ScheduleAdminTests : IDisposable
{
    private readonly string _root;

    public ScheduleAdminTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-sched-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly CallerContext Alice = new("S-1-5-21-ALICE", IsAdmin: false);
    private static readonly CallerContext Bob = new("S-1-5-21-BOB", IsAdmin: false);
    private static readonly CallerContext Admin = new("S-1-5-18", IsAdmin: true);

    /// <summary>Фейковый исполнитель: задачу не выполняет реально, лишь рапортует успех.</summary>
    private sealed class NoOpRunner : IJobRunner
    {
        public Task<JobOutcome> RunAsync(Job job, CancellationToken ct)
            => Task.FromResult(new JobOutcome(true, 0, 0, null, null));
    }

    private (ServiceHandlers handlers, JobManager jobs) Build()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var storages = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        var schedules = new ScheduleStore(storages.Protector, Path.Combine(_root, "cfg", "schedules.json"));
        var jobs = new JobManager(new NoOpRunner(), rid => new JobChannel(history, rid));
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), storages, "inst", "1.2.0", schedules: schedules);
        return (handlers, jobs);
    }

    private static ScheduleInput Make(string id = "s1", string name = "Ночной", string? passphrase = null) => new()
    {
        Id = id, Name = name, Kind = "Daily", Hour = 3, Minute = 0,
        ModuleIds = ["obs"], TargetStorageIds = ["NAS"], NewPassphrase = passphrase,
    };

    [Fact]
    public async Task Upsert_List_Delete_RoundTrip_Stamps_Owner()
    {
        var (handlers, jobs) = Build();
        await using var _ = jobs;

        await handlers.UpsertScheduleAsync(Make(), Alice, default);

        var listed = await handlers.ListSchedulesAsync(Alice, default);
        var s = Assert.Single(listed);
        Assert.Equal("s1", s.Id);
        Assert.Equal("S-1-5-21-ALICE", s.OwnerSid); // владелец = создатель
        Assert.False(s.HasPassphrase);
        Assert.NotNull(s.NextRun);                  // Daily → есть будущее время

        await handlers.DeleteScheduleAsync(new DeleteByIdRequest { Id = "s1" }, Alice, default);
        Assert.Empty(await handlers.ListSchedulesAsync(Alice, default));
    }

    [Fact]
    public async Task Empty_Id_Or_Name_Is_BadRequest()
    {
        var (handlers, jobs) = Build();
        await using var _ = jobs;
        await Assert.ThrowsAsync<IpcFaultException>(() => handlers.UpsertScheduleAsync(Make(id: ""), Alice, default));
        await Assert.ThrowsAsync<IpcFaultException>(() => handlers.UpsertScheduleAsync(Make(name: ""), Alice, default));
    }

    [Fact]
    public async Task Other_User_Cannot_See_Edit_Delete_Or_Run_Anothers_Schedule()
    {
        var (handlers, jobs) = Build();
        await using var _ = jobs;
        await handlers.UpsertScheduleAsync(Make(), Alice, default);

        Assert.Empty(await handlers.ListSchedulesAsync(Bob, default)); // чужое не видно

        await Assert.ThrowsAsync<IpcFaultException>(
            () => handlers.UpsertScheduleAsync(Make(name: "взлом"), Bob, default));
        await Assert.ThrowsAsync<IpcFaultException>(
            () => handlers.DeleteScheduleAsync(new DeleteByIdRequest { Id = "s1" }, Bob, default));
        await Assert.ThrowsAsync<IpcFaultException>(
            () => handlers.RunScheduleNowAsync(new RunScheduleNowRequest { ScheduleId = "s1" }, Bob, default));

        // Админ видит и управляет чужими.
        Assert.Single(await handlers.ListSchedulesAsync(Admin, default));
    }

    [Fact]
    public async Task RunNow_Enqueues_Scheduled_Job_Under_Owner()
    {
        var (handlers, jobs) = Build();
        await using var _ = jobs;
        await handlers.UpsertScheduleAsync(Make(), Alice, default);

        // Запустить может и админ — но задача должна идти под владельцем (per-SID резолв).
        var resp = await handlers.RunScheduleNowAsync(new RunScheduleNowRequest { ScheduleId = "s1" }, Admin, default);
        Assert.NotEmpty(resp.JobId);

        var status = await handlers.GetJobAsync(new GetJobRequest { JobId = resp.JobId }, Admin, default);
        Assert.Equal("Scheduled", status.JobOrigin);
        Assert.Equal("S-1-5-21-ALICE", status.OwnerSid); // профиль владельца, не админа
    }

    [Fact]
    public async Task RunNow_On_Encrypted_Schedule_Owner_Succeeds_NonOwner_Rejected()
    {
        var (handlers, jobs) = Build();
        await using var _ = jobs;
        await handlers.UpsertScheduleAsync(Make(passphrase: "secret"), Alice, default);
        Assert.True(Assert.Single(await handlers.ListSchedulesAsync(Alice, default)).HasPassphrase);

        // Владелец запускает зашифрованное (служба расшифрует машинным ключом).
        var resp = await handlers.RunScheduleNowAsync(new RunScheduleNowRequest { ScheduleId = "s1" }, Alice, default);
        Assert.NotEmpty(resp.JobId);
        Assert.Equal("Scheduled", (await handlers.GetJobAsync(new GetJobRequest { JobId = resp.JobId }, Alice, default)).JobOrigin);

        // Админ (НЕ владелец) зашифрованное run-now — запрещено (фраза принадлежит владельцу).
        await Assert.ThrowsAsync<IpcFaultException>(
            () => handlers.RunScheduleNowAsync(new RunScheduleNowRequest { ScheduleId = "s1" }, Admin, default));
    }

    [Fact]
    public async Task Edit_Keeps_Owner_And_Passphrase_When_NewPassphrase_Null()
    {
        var (handlers, jobs) = Build();
        await using var _ = jobs;
        await handlers.UpsertScheduleAsync(Make(passphrase: "secret"), Alice, default);

        // Правка без передачи фразы (null) — шифрование сохраняется, владелец прежний.
        await handlers.UpsertScheduleAsync(Make(name: "Переименовано") with { NewPassphrase = null }, Alice, default);

        var s = Assert.Single(await handlers.ListSchedulesAsync(Alice, default));
        Assert.True(s.HasPassphrase);
        Assert.Equal("S-1-5-21-ALICE", s.OwnerSid);
    }
}
