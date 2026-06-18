using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;

namespace eBackup.Service.Jobs;

/// <summary>
/// Мост между внутренними нотами задачи (<see cref="JobNote"/>) и IPC-стримингом: подписывается
/// на <see cref="JobChannel"/> задачи и отдаёт ноты уже в виде note-фреймов (Phase/Log/State).
/// Доступ — по OwnerSid (как и остальные операции).
/// </summary>
public sealed class JobStreamAdapter : IJobStream
{
    private readonly JobManager _jobs;

    public JobStreamAdapter(JobManager jobs) => _jobs = jobs;

    public JobSubscription? Attach(string jobId, long fromSeq, CallerContext caller)
    {
        var job = _jobs.Get(jobId);
        if (job is null || (!caller.IsAdmin && job.OwnerSid != caller.OwnerSid))
            return null;

        var sub = job.Channel.Subscribe(fromSeq);
        var finished = job.State is JobState.Completed or JobState.CompletedWithErrors
            or JobState.Failed or JobState.Cancelled;

        var backlog = sub.Backlog.Select(n => ToFrame(jobId, n)).ToList();
        return new JobSubscription(sub.LowestRetainedSeq, sub.NextSeq, finished, backlog, MapLive(jobId, sub.Live), sub.Dispose);
    }

    private static async IAsyncEnumerable<Frame> MapLive(
        string jobId, ChannelReader<JobNote> live, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var n in live.ReadAllAsync(ct).ConfigureAwait(false))
            yield return ToFrame(jobId, n);
    }

    private static Frame ToFrame(string jobId, JobNote n)
    {
        var (op, body) = n.Kind switch
        {
            JobNoteKind.Phase => (IpcNotes.Phase,
                JsonSerializer.SerializeToElement(
                    new PhaseNote { JobId = jobId, Seq = n.Seq, Phase = n.Text ?? "", ProgressFraction = n.Fraction },
                    IpcJsonContext.Default.PhaseNote)),
            JobNoteKind.Log => (IpcNotes.Log,
                JsonSerializer.SerializeToElement(
                    new LogNote { JobId = jobId, Seq = n.Seq, Text = n.Text ?? "" },
                    IpcJsonContext.Default.LogNote)),
            _ => (IpcNotes.State,
                JsonSerializer.SerializeToElement(
                    new StateChangedNote { JobId = jobId, Seq = n.Seq, State = n.State ?? "" },
                    IpcJsonContext.Default.StateChangedNote)),
        };
        return new Frame { Kind = FrameKinds.Note, Op = op, Seq = n.Seq, Body = body };
    }
}
