using eBackup.Ipc.Contracts;

namespace eBackup.Ipc.Server;

/// <summary>
/// Источник живого потока нотификаций задачи. Реализует служба, преобразуя свои ноты в
/// note-фреймы (Phase/Log/State). Соединение лишь подписывается и качает фреймы клиенту.
/// </summary>
public interface IJobStream
{
    /// <summary>Подписаться на задачу. null — задачи нет или у вызывающего нет прав.</summary>
    JobSubscription? Attach(string jobId, long fromSeq, CallerContext caller);
}

/// <summary>
/// Подписка: координаты для реплея, бэклог уже в виде note-фреймов, живой поток note-фреймов
/// и действие отписки. Бэклог снят и живой поток зарегистрирован атомарно (без пропусков/дублей).
/// </summary>
public sealed record JobSubscription(
    long LowestRetainedSeq,
    long NextSeq,
    bool Finished,
    IReadOnlyList<Frame> Backlog,
    IAsyncEnumerable<Frame> Live,
    Action Detach);
