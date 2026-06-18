using System.Collections.Concurrent;
using System.Threading.Channels;
using eBackup.Ipc.Contracts;

namespace eBackup.Service.Jobs;

/// <summary>
/// Очередь и жизненный цикл задач бэкапа. ОДНА активная задача (FIFO) — совпадает с реальностью
/// движка (общий temp, до двух копий архива при шифровании). Задача переживает закрытие GUI:
/// рабочий поток доводит её до конца независимо от клиентов. onStateChanged — хук для журнала
/// истории и (на S4d) рассылки нотификаций.
/// </summary>
public sealed class JobManager : IAsyncDisposable
{
    private readonly IJobRunner _runner;
    private readonly Func<string, JobChannel> _channelFactory;
    private readonly Action<Job>? _onStateChanged;
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private long _seq;

    public JobManager(IJobRunner runner, Func<string, JobChannel> channelFactory, Action<Job>? onStateChanged = null)
    {
        _runner = runner;
        _channelFactory = channelFactory;
        _onStateChanged = onStateChanged;
        _worker = Task.Run(WorkerLoopAsync);
    }

    public Job Enqueue(StartBackupRequest req, string ownerSid, string origin = "Interactive")
    {
        var runId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var job = new Job
        {
            Seq = Interlocked.Increment(ref _seq),
            JobId = "job-" + Guid.NewGuid().ToString("N"),
            RunId = runId,
            OwnerSid = ownerSid,
            Trigger = string.IsNullOrEmpty(req.Trigger) ? "вручную" : req.Trigger,
            Origin = origin,
            Request = req,
            Channel = _channelFactory(runId),
        };
        _jobs[job.JobId] = job;
        Notify(job);                 // State=Queued — журнал увидит «прерванный останется виден»
        _queue.Writer.TryWrite(job);
        return job;
    }

    public Job? Get(string jobId) => _jobs.TryGetValue(jobId, out var j) ? j : null;

    /// <summary>Сколько незавершённых задач стоит впереди (0 — выполняется/следующая).</summary>
    public int Position(Job job)
        => _jobs.Values.Count(j => j.Seq < job.Seq && j.State is JobState.Queued or JobState.Running);

    public IReadOnlyList<Job> List(string callerSid, bool isAdmin, bool includeFinished)
        => _jobs.Values
            .Where(j => isAdmin || j.OwnerSid == callerSid)
            .Where(j => includeFinished || j.State is JobState.Queued or JobState.Running)
            .OrderByDescending(j => j.Seq)
            .ToList();

    /// <summary>Отмена. Разрешена владельцу или администратору. Очередную/идущую — снимет рабочий поток.</summary>
    public bool Cancel(string jobId, string callerSid, bool isAdmin)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        if (!isAdmin && job.OwnerSid != callerSid) return false;
        job.IsCancelling = true;
        job.Cts.Cancel();
        return true;
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                if (job.Cts.IsCancellationRequested) // отменена пока стояла в очереди
                {
                    Finish(job, JobState.Cancelled, null);
                    continue;
                }

                job.StartedAt = DateTimeOffset.Now;
                SetState(job, JobState.Running);

                JobOutcome outcome;
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token, _shutdown.Token);
                    outcome = await _runner.RunAsync(job, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Finish(job, JobState.Cancelled, null);
                    continue;
                }
                catch (Exception ex)
                {
                    Finish(job, JobState.Failed, new JobOutcome(false, 0, 0, null, ex.Message));
                    continue;
                }

                var state = !outcome.Success ? JobState.Failed
                    : outcome.SkippedFiles > 0 ? JobState.CompletedWithErrors
                    : JobState.Completed;
                Finish(job, state, outcome);
            }
        }
        catch (OperationCanceledException) { /* остановка службы */ }
    }

    private void SetState(Job job, JobState s)
    {
        job.State = s;
        Notify(job);
    }

    private void Finish(Job job, JobState s, JobOutcome? outcome)
    {
        if (outcome is not null) job.Outcome = outcome;
        job.FinishedAt = DateTimeOffset.Now;
        job.State = s;
        Notify(job);
        job.Channel.Complete(); // терминал — закрываем живые подписки
    }

    private void Notify(Job job)
    {
        // Нота смены состояния в шину задачи (для живого прогресса) + хук журнала истории.
        try { job.Channel.EmitState(job.State.ToString(), FractionFor(job.State)); } catch { }
        try { _onStateChanged?.Invoke(job); } catch { /* журнал/нотификации не должны ронять задачу */ }
    }

    private static double FractionFor(JobState s) => s switch
    {
        JobState.Running => 0.5,
        JobState.Completed or JobState.CompletedWithErrors or JobState.Failed or JobState.Cancelled => 1.0,
        _ => 0.0,
    };

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        try { await _worker.ConfigureAwait(false); } catch { }
        _shutdown.Dispose();
    }
}
