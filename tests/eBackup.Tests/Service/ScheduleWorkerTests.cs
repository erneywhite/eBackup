using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.History;
using eBackup.Core.Scheduling;
using eBackup.Core.Security;
using eBackup.Ipc.Contracts;
using eBackup.Service;
using eBackup.Service.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace eBackup.Tests.Service;

public sealed class ScheduleWorkerTests : IDisposable
{
    private readonly string _root;

    public ScheduleWorkerTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-swk-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string p) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(p));
        public string Unprotect(string p) => Encoding.UTF8.GetString(Convert.FromBase64String(p["enc:".Length..]));
    }

    /// <summary>Протектор, чей Unprotect всегда бросает MachineKeyException (имитация недоступного ключа).</summary>
    private sealed class ThrowingKeyProtector : ISecretProtector
    {
        public string Protect(string p) => "enc:" + p;
        public string Unprotect(string p) => throw new eBackup.Security.MachineKeyException("ключ недоступен");
    }

    private sealed class FakeIdle(TimeSpan idle) : IIdleSource
    {
        public Task<TimeSpan> GetIdleAsync(CancellationToken ct) => Task.FromResult(idle);
    }

    private sealed class NoOpRunner : IJobRunner
    {
        public Task<JobOutcome> RunAsync(Job job, CancellationToken ct) => Task.FromResult(new JobOutcome(true, 0, 0, null, null));
    }

    private (ScheduleWorker worker, ScheduleStore store, JobManager jobs) Build(TimeSpan idle)
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var store = new ScheduleStore(new FakeProtector(), Path.Combine(_root, "cfg", "schedules.json"));
        var jobs = new JobManager(new NoOpRunner(), rid => new JobChannel(history, rid));
        var worker = new ScheduleWorker(store, jobs, new FakeIdle(idle), NullLogger<ScheduleWorker>.Instance);
        return (worker, store, jobs);
    }

    private static BackupSchedule Daily(string id, DateTime lastRun, string? owner = "S-1-5-21-ALICE", string? pass = null) => new()
    {
        Id = id, Name = id, Kind = ScheduleKind.Daily, Hour = 3, Minute = 0,
        OwnerSid = owner, ProtectedPassphrase = pass, ModuleIds = ["obs"], LastRunAt = lastRun,
    };

    [Fact]
    public async Task Due_Daily_Schedule_Is_Enqueued_And_Stamped()
    {
        var (worker, store, jobs) = Build(TimeSpan.Zero);
        await using var _ = jobs;
        await store.SaveAllAsync([Daily("s1", new DateTime(2026, 6, 18, 3, 0, 0))]);

        var now = new DateTime(2026, 6, 19, 3, 0, 20); // в окне Grace после 03:00
        var queued = await worker.TickAsync(now, default);

        Assert.Equal(1, queued);
        var job = Assert.Single(jobs.List("S-1-5-21-ALICE", isAdmin: false, includeFinished: true));
        Assert.Equal("Scheduled", job.Origin);
        Assert.Equal("S-1-5-21-ALICE", job.OwnerSid);

        // LastRunAt проштампован «сейчас» → повторно не сработает.
        var reloaded = Assert.Single(await store.LoadAsync());
        Assert.Equal(now, reloaded.LastRunAt);
        Assert.Equal(0, await worker.TickAsync(now.AddMinutes(1), default));
    }

    [Fact]
    public async Task Not_Due_Schedule_Is_Skipped()
    {
        var (worker, store, jobs) = Build(TimeSpan.Zero);
        await using var _ = jobs;
        await store.SaveAllAsync([Daily("s1", new DateTime(2026, 6, 19, 3, 0, 0))]); // уже отработал сегодня

        Assert.Equal(0, await worker.TickAsync(new DateTime(2026, 6, 19, 9, 0, 0), default));
    }

    [Fact]
    public async Task Encrypted_Schedule_Fires_And_Is_Stamped_When_Key_Available()
    {
        var (worker, store, jobs) = Build(TimeSpan.Zero);
        await using var _ = jobs;
        var last = new DateTime(2026, 6, 18, 3, 0, 0);
        await store.SaveAllAsync([Daily("s1", last, pass: store.ProtectPassphrase("secret"))]);

        var now = new DateTime(2026, 6, 19, 3, 0, 20);
        Assert.Equal(1, await worker.TickAsync(now, default)); // S7: зашифрованное теперь срабатывает

        var job = Assert.Single(jobs.List("S-1-5-21-ALICE", isAdmin: false, includeFinished: true));
        Assert.True(job.RequiresEncryption);
        Assert.Equal(Encoding.UTF8.GetBytes("secret"), job.ResolvedPassphrase); // фраза расшифрована машинным ключом
        Assert.Equal(now, Assert.Single(await store.LoadAsync()).LastRunAt);     // проштамповано
    }

    [Fact]
    public async Task Encrypted_With_Unavailable_Key_Is_Skipped_Without_Stamp_And_Sibling_Still_Stamped()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var store = new ScheduleStore(new ThrowingKeyProtector(), Path.Combine(_root, "cfg", "schedules.json"));
        await using var jobs = new JobManager(new NoOpRunner(), rid => new JobChannel(history, rid));
        var worker = new ScheduleWorker(store, jobs, new FakeIdle(TimeSpan.Zero), NullLogger<ScheduleWorker>.Instance);

        var encLast = new DateTime(2026, 6, 18, 3, 0, 0);
        await store.SaveAllAsync(
        [
            Daily("plain", new DateTime(2026, 6, 18, 3, 0, 0)),       // обычное — сработает и проштампуется
            Daily("enc", encLast, pass: "enc:whatever"),             // зашифр. — ключ бросит MachineKeyException
        ]);

        var now = new DateTime(2026, 6, 19, 3, 0, 20);
        Assert.Equal(1, await worker.TickAsync(now, default)); // поставлено только обычное; тик не упал

        var reloaded = (await store.LoadAsync()).ToDictionary(s => s.Id);
        Assert.Equal(now, reloaded["plain"].LastRunAt);   // штамп успешного СОХРАНЁН (SaveAllAsync в finally)
        Assert.Equal(encLast, reloaded["enc"].LastRunAt);  // зашифр. НЕ проштамповано (оживёт после восстановления ключа)
    }

    [Fact]
    public async Task Owner_Less_Schedule_Is_Skipped()
    {
        var (worker, store, jobs) = Build(TimeSpan.Zero);
        await using var _ = jobs;
        await store.SaveAllAsync([Daily("s1", new DateTime(2026, 6, 18, 3, 0, 0), owner: null)]);

        Assert.Equal(0, await worker.TickAsync(new DateTime(2026, 6, 19, 3, 0, 20), default));
    }

    [Fact]
    public async Task DailyWhenIdle_Fires_Only_When_Idle_Source_Reports_Idle()
    {
        var idleSchedule = new BackupSchedule
        {
            Id = "idle", Name = "idle", Kind = ScheduleKind.DailyWhenIdle, IdleMinutes = 5,
            OwnerSid = "S-1-5-21-ALICE", ModuleIds = ["obs"], LastRunAt = new DateTime(2026, 6, 18, 12, 0, 0),
        };
        var now = new DateTime(2026, 6, 19, 14, 0, 0);

        // Источник «занят» (Zero) — не срабатывает.
        var (busy, busyStore, busyJobs) = Build(TimeSpan.Zero);
        await using (busyJobs)
        {
            await busyStore.SaveAllAsync([idleSchedule]);
            Assert.Equal(0, await busy.TickAsync(now, default));
        }

        // Источник «в простое 10 мин» — срабатывает.
        var (idleW, idleStore, idleJobs) = Build(TimeSpan.FromMinutes(10));
        await using (idleJobs)
        {
            await idleStore.SaveAllAsync([idleSchedule]);
            Assert.Equal(1, await idleW.TickAsync(now, default));
        }
    }
}
