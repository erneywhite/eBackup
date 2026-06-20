using eBackup.Core.History;
using eBackup.Localization;

namespace eBackup.Service.Jobs;

/// <summary>
/// Восстановление после сбоя: если служба упала/перезапустилась во время бэкапа, в журнале
/// останется запись с State=Running без FinishedAt (in-memory задача уже умерла). На старте
/// помечаем такие как Interrupted, чтобы «История» не показывала вечный «в процессе».
/// </summary>
public static class CrashRecovery
{
    public static async Task<int> SweepInterruptedAsync(HistoryStore history)
    {
        var runs = await history.LoadAsync().ConfigureAwait(false);
        var fixedCount = 0;
        foreach (var r in runs)
        {
            // Незавершённая = нет FinishedAt и состояние «идёт» (или старая запись без State/итога).
            var unfinished = r.FinishedAt is null
                && (r.State is "Running" or "Queued" || (r.State is null && r.Success is null));
            if (!unfinished) continue;

            r.State = "Interrupted";
            r.Success = false;
            r.FinishedAt = DateTimeOffset.Now;
            r.Error ??= L.Get("Svc_InterruptedServiceRestarted");
            await history.SaveRunAsync(r).ConfigureAwait(false);
            fixedCount++;
        }
        return fixedCount;
    }
}
