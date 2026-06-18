using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.History;
using eBackup.Ipc.Contracts;
using eBackup.Service.Jobs;
using Xunit;

namespace eBackup.Tests.Service;

public class JobManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ebk-jm-{Guid.NewGuid():N}");
    private readonly HistoryStore _history;

    public JobManagerTests() => _history = new HistoryStore(_dir);
    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { } }

    private JobChannel Channel(string runId) => new(_history, runId);
    // Исполнитель, который блокируется до Release (или до отмены через ct).
    private sealed class BlockingRunner : IJobRunner
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JobOutcome Outcome = new(true, 0, 100, "a.ebk", null);
        public int Started;

        public async Task<JobOutcome> RunAsync(Job job, CancellationToken ct)
        {
            Interlocked.Increment(ref Started);
            await Release.Task.WaitAsync(ct);
            return Outcome;
        }
    }

    private static async Task WaitFor(Func<bool> cond, string what, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(20);
        Assert.True(cond(), $"не дождались: {what}");
    }

    private static StartBackupRequest Req(string trigger = "t") => new() { Trigger = trigger, ModuleIds = ["m"] };

    [Fact]
    public async Task Job_Runs_To_Completion()
    {
        var runner = new BlockingRunner();
        runner.Release.TrySetResult(); // не блокируем
        await using var jm = new JobManager(runner, Channel);

        var job = jm.Enqueue(Req(), "S-1-5-21-1");
        await WaitFor(() => job.State == JobState.Completed, "Completed");
        Assert.Equal(100, job.Outcome!.SizeBytes);
        Assert.NotNull(job.FinishedAt);
    }

    [Fact]
    public async Task Skipped_Files_Give_CompletedWithErrors()
    {
        var runner = new BlockingRunner { Outcome = new JobOutcome(true, 2, 50, "a.ebk", null) };
        runner.Release.TrySetResult();
        await using var jm = new JobManager(runner, Channel);

        var job = jm.Enqueue(Req(), "S-1-5-21-1");
        await WaitFor(() => job.State == JobState.CompletedWithErrors, "CompletedWithErrors");
    }

    [Fact]
    public async Task Failed_Outcome_Gives_Failed()
    {
        var runner = new BlockingRunner { Outcome = new JobOutcome(false, 0, 0, null, "boom") };
        runner.Release.TrySetResult();
        await using var jm = new JobManager(runner, Channel);

        var job = jm.Enqueue(Req(), "S-1-5-21-1");
        await WaitFor(() => job.State == JobState.Failed, "Failed");
        Assert.Equal("boom", job.Outcome!.Error);
    }

    [Fact]
    public async Task Single_Active_Job_Is_FIFO()
    {
        var runner = new BlockingRunner(); // блокируется
        await using var jm = new JobManager(runner, Channel);

        var a = jm.Enqueue(Req("a"), "S-1-5-21-1");
        var b = jm.Enqueue(Req("b"), "S-1-5-21-1");

        await WaitFor(() => a.State == JobState.Running, "A Running");
        Assert.Equal(JobState.Queued, b.State); // одна активная — второй ждёт
        Assert.Equal(1, runner.Started);
        Assert.Equal(1, jm.Position(b));         // один впереди

        runner.Release.TrySetResult();
        await WaitFor(() => b.State == JobState.Completed, "B Completed");
    }

    [Fact]
    public async Task Cancel_Running_Job()
    {
        var runner = new BlockingRunner(); // блокируется, честно ждёт ct
        await using var jm = new JobManager(runner, Channel);

        var job = jm.Enqueue(Req(), "S-1-5-21-1");
        await WaitFor(() => job.State == JobState.Running, "Running");

        Assert.True(jm.Cancel(job.JobId, "S-1-5-21-1", isAdmin: false));
        await WaitFor(() => job.State == JobState.Cancelled, "Cancelled");
    }

    [Fact]
    public async Task Cancel_Rejects_Non_Owner()
    {
        var runner = new BlockingRunner();
        await using var jm = new JobManager(runner, Channel);

        var job = jm.Enqueue(Req(), "S-1-5-21-1");
        await WaitFor(() => job.State == JobState.Running, "Running");

        Assert.False(jm.Cancel(job.JobId, "S-1-5-21-OTHER", isAdmin: false));
        Assert.NotEqual(JobState.Cancelled, job.State);
        runner.Release.TrySetResult();
    }

    [Fact]
    public async Task List_Is_Owner_Scoped_Admin_Sees_All()
    {
        var runner = new BlockingRunner();
        runner.Release.TrySetResult();
        await using var jm = new JobManager(runner, Channel);

        var a = jm.Enqueue(Req(), "S-1-5-21-AAA");
        var b = jm.Enqueue(Req(), "S-1-5-21-BBB");
        await WaitFor(() => a.State == JobState.Completed && b.State == JobState.Completed, "both done");

        var listA = jm.List("S-1-5-21-AAA", isAdmin: false, includeFinished: true);
        Assert.Contains(listA, j => j.JobId == a.JobId);
        Assert.DoesNotContain(listA, j => j.JobId == b.JobId);

        Assert.True(jm.List("x", isAdmin: true, includeFinished: true).Count >= 2);
    }
}
