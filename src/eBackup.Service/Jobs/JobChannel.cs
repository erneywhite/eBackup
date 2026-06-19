using System.Threading.Channels;
using eBackup.Core.History;

namespace eBackup.Service.Jobs;

/// <summary>
/// Шина нотификаций одной задачи: присваивает монотонный seq, пишет строку в журнал истории
/// (канал — ЕДИНСТВЕННЫЙ писатель этого runId, поэтому seq == номер строки журнала, и
/// GetRunLog по seq совпадает с потоком нот), держит кольцевой буфер последних нот для
/// реплея при подключении и раздаёт ноты живым подписчикам (через AttachToJob).
/// </summary>
public sealed class JobChannel : IJobSink
{
    private readonly HistoryStore _history;
    private readonly string _runId;
    private readonly int _ringCap;
    private readonly object _lock = new();
    private readonly LinkedList<JobNote> _ring = new();
    private readonly List<Channel<JobNote>> _subs = new();
    private long _seq;
    private bool _completed;

    public JobChannel(HistoryStore history, string runId, int ringCap = 2000)
    {
        _history = history;
        _runId = runId;
        _ringCap = ringCap;
    }

    public void Phase(string text, double fraction) => Emit(JobNoteKind.Phase, text, fraction, null);
    public void Log(string text) => Emit(JobNoteKind.Log, text, 0, null);
    public long EmitState(string state, double fraction) => Emit(JobNoteKind.State, null, fraction, state);

    public long Emit(string kind, string? text, double fraction, string? state)
    {
        JobNote note;
        Channel<JobNote>[] subs;
        lock (_lock)
        {
            note = new JobNote(++_seq, kind, _runId, text, fraction, state);
            _ring.AddLast(note);
            while (_ring.Count > _ringCap) _ring.RemoveFirst();
            subs = _subs.ToArray();
            // Журнальная строка ПОД локом → seq == номер строки (мы единственный писатель runId).
            _history.AppendLog(_runId, FormatLine(note));
        }
        foreach (var s in subs) s.Writer.TryWrite(note);
        return note.Seq;
    }

    private static string FormatLine(JobNote n) => n.Kind switch
    {
        JobNoteKind.State or JobNoteKind.Completed => $"=== {n.State} ===",
        _ => n.Text ?? string.Empty,
    };

    /// <summary>
    /// Подписаться на ноты после <paramref name="fromSeq"/>. Под локом: регистрируем живого
    /// подписчика И снимаем бэклог из кольца — так ни одна нота не проскользнёт между ними.
    /// Если задача уже завершена — живой канал сразу закрыт, отдаём только бэклог.
    /// </summary>
    public Subscription Subscribe(long fromSeq)
    {
        lock (_lock)
        {
            var live = Channel.CreateUnbounded<JobNote>(new UnboundedChannelOptions { SingleReader = true });
            var backlog = _ring.Where(n => n.Seq > fromSeq).ToList();
            var lowest = _ring.Count > 0 ? _ring.First!.Value.Seq : _seq + 1;

            if (_completed) live.Writer.TryComplete();   // будущих нот не будет
            else _subs.Add(live);

            return new Subscription(lowest, _seq, backlog, live.Reader, () => Unsubscribe(live));
        }
    }

    private void Unsubscribe(Channel<JobNote> ch)
    {
        lock (_lock) { _subs.Remove(ch); }
        ch.Writer.TryComplete();
    }

    /// <summary>Задача завершилась — закрыть живые подписки (бэклог в кольце остаётся для опоздавших).</summary>
    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            foreach (var s in _subs) s.Writer.TryComplete();
            _subs.Clear();
        }
    }
}

/// <summary>Результат подписки: координаты + бэклог + живой поток + отписка.</summary>
public sealed record Subscription(
    long LowestRetainedSeq,
    long NextSeq,
    IReadOnlyList<JobNote> Backlog,
    ChannelReader<JobNote> Live,
    Action Dispose);
