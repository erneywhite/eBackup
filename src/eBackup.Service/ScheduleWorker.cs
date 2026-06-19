using eBackup.Core.Scheduling;
using eBackup.Service.Handlers;
using eBackup.Service.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace eBackup.Service;

/// <summary>
/// Планировщик службы: раз в минуту проверяет расписания и ставит «созревшие» в ОБЩУЮ очередь задач
/// (тот же <see cref="JobManager"/>, что у IPC) — поэтому бэкап идёт даже без вошедшего пользователя.
/// LastRunAt штампуется при ПОСТАНОВКЕ в очередь: без повторов и без догона пропущенных (политика S6).
/// Зашифрованные расписания пока пропускаются и НЕ штампуются (заработают на S7).
/// </summary>
public sealed class ScheduleWorker : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private readonly ScheduleStore _schedules;
    private readonly JobManager _jobs;
    private readonly IIdleSource _idle;
    private readonly ILogger<ScheduleWorker> _log;

    public ScheduleWorker(ScheduleStore schedules, JobManager jobs, IIdleSource idle, ILogger<ScheduleWorker> log)
    {
        _schedules = schedules;
        _jobs = jobs;
        _idle = idle;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Планировщик бэкапов запущен (тик {Sec} с).", (int)TickInterval.TotalSeconds);
        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try { await TickAsync(DateTime.Now, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Тик планировщика завершился с ошибкой"); }
        }
        while (await WaitNextAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitNextAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>
    /// Один проход планировщика: ставит в очередь созревшие расписания и штампует их LastRunAt.
    /// Возвращает число поставленных задач (для логов/тестов). Публичный — вызывается напрямую в тестах.
    /// </summary>
    public async Task<int> TickAsync(DateTime now, CancellationToken ct)
    {
        var idle = _idle.GetIdle();
        var list = (await _schedules.LoadAsync(ct).ConfigureAwait(false)).ToList();

        var queued = 0;
        var changed = false;
        for (var i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (!ScheduleTiming.IsDue(s, now, idle))
                continue;

            if (s.ProtectedPassphrase is not null)
            {
                // S6: зашифрованные бэкапы по расписанию не запускаем и НЕ штампуем — оживут на S7.
                _log.LogInformation("Расписание «{Name}»: шифрование по расписанию пока не поддержано — пропуск.", s.Name);
                continue;
            }
            if (string.IsNullOrEmpty(s.OwnerSid))
            {
                _log.LogWarning("Расписание «{Name}»: не задан владелец (OwnerSid) — пропуск.", s.Name);
                continue;
            }

            // Под профилем владельца (per-SID резолв источников), помечаем Scheduled.
            _jobs.Enqueue(ScheduleInputMapper.ToBackupRequest(s), s.OwnerSid, origin: "Scheduled");
            list[i] = s with { LastRunAt = now };  // штамп при постановке → без повтора и без догона
            changed = true;
            queued++;
            _log.LogInformation("Расписание «{Name}»: бэкап поставлен в очередь.", s.Name);
        }

        if (changed)
            await _schedules.SaveAllAsync(list, ct).ConfigureAwait(false);
        return queued;
    }
}
