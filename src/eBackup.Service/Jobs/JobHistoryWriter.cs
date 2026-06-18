using eBackup.Core.History;

namespace eBackup.Service.Jobs;

/// <summary>
/// Подписчик на смену состояния задачи: апсертит запись в журнал истории (Queued при постановке,
/// затем Running и терминал). Записи сериализованы одним семафором, чтобы параллельные сохранения
/// не теряли друг друга. Журнал вспомогательный — ошибки глотаем, бэкап не роняем.
/// </summary>
public sealed class JobHistoryWriter
{
    private readonly HistoryStore _history;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JobHistoryWriter(HistoryStore history) => _history = history;

    public void OnStateChanged(Job job) => _ = PersistAsync(JobMapping.ToRecord(job));

    private async Task PersistAsync(BackupRunRecord record)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _history.SaveRunAsync(record).ConfigureAwait(false); }
        catch { /* журнал не должен ронять задачу */ }
        finally { _gate.Release(); }
    }
}
