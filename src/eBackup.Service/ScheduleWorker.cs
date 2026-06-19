using eBackup.Core.Scheduling;
using eBackup.Platform;
using eBackup.Security;
using eBackup.Service.Handlers;
using eBackup.Service.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace eBackup.Service;

/// <summary>
/// Планировщик службы: раз в минуту проверяет расписания и ставит «созревшие» в ОБЩУЮ очередь задач
/// (тот же <see cref="JobManager"/>, что у IPC) — поэтому бэкап идёт даже без вошедшего пользователя.
/// LastRunAt штампуется при ПОСТАНОВКЕ в очередь: без повторов и без догона пропущенных (политика S6).
/// Зашифрованные расписания служба расшифровывает машинным ключом сама (S7); если ключ недоступен —
/// расписание пропускается БЕЗ штампа (оживёт после восстановления ключа), тик при этом не падает.
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
        await StampSleepingEncryptedOnceAsync(stoppingToken).ConfigureAwait(false);
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
        var idle = await _idle.GetIdleAsync(ct).ConfigureAwait(false);
        var list = (await _schedules.LoadAsync(ct).ConfigureAwait(false)).ToList();

        var queued = 0;
        var changed = false;
        try
        {
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (!ScheduleTiming.IsDue(s, now, idle))
                    continue;
                if (string.IsNullOrEmpty(s.OwnerSid))
                {
                    _log.LogWarning("Расписание «{Name}»: не задан владелец (OwnerSid) — пропуск.", s.Name);
                    continue;
                }

                try
                {
                    // Зашифрованное — расшифровываем фразу машинным ключом синхронно ЗДЕСЬ (а не в раннере),
                    // чтобы при отсутствии ключа НЕ штамповать и не зацикливать постановку.
                    ScheduleInputMapper.EnqueueScheduled(_jobs, _schedules, s, s.OwnerSid);
                }
                catch (MachineKeyException ex)
                {
                    // Ключ недоступен/сменился → пропуск БЕЗ штампа (оживёт после восстановления ключа), тик не валим.
                    _log.LogWarning(ex, "Расписание «{Name}»: машинный ключ недоступен — пропуск без штампа.", s.Name);
                    continue;
                }

                list[i] = s with { LastRunAt = now };  // штамп при постановке → без повтора и без догона
                changed = true;
                queued++;
                _log.LogInformation("Расписание «{Name}»: бэкап поставлен в очередь.", s.Name);
            }
        }
        finally
        {
            // Сохраняем ГАРАНТИРОВАННО: иначе упавшее расписание потеряло бы штампы успешных в этом же тике.
            if (changed)
                await _schedules.SaveAllAsync(list, ct).ConfigureAwait(false);
        }
        return queued;
    }

    /// <summary>
    /// Одноразовая выкатка S7: ранее «спавшие» зашифрованные расписания (S6 их пропускал и не штамповал)
    /// помечаем «выполнено сейчас», чтобы при включении шифрования они НЕ выстрелили пачкой задним числом
    /// (у EveryHours/DailyWhenIdle нет grace-окна). Маркер в ProgramData делает это ровно один раз.
    /// </summary>
    private async Task StampSleepingEncryptedOnceAsync(CancellationToken ct)
    {
        var marker = Path.Combine(AppPaths.MachineConfigDir, "s7-encrypted-stamped.marker");
        if (File.Exists(marker))
            return;
        try
        {
            var now = DateTime.Now;
            var list = (await _schedules.LoadAsync(ct).ConfigureAwait(false)).ToList();
            var changed = false;
            for (var i = 0; i < list.Count; i++)
                if (list[i].ProtectedPassphrase is not null)
                {
                    list[i] = list[i] with { LastRunAt = now };
                    changed = true;
                }
            if (changed)
            {
                await _schedules.SaveAllAsync(list, ct).ConfigureAwait(false);
                _log.LogInformation("S7-выкатка: проштамповано зашифрованных расписаний — без залпа задним числом.");
            }
            Directory.CreateDirectory(AppPaths.MachineConfigDir);
            await File.WriteAllTextAsync(marker, now.ToString("o"), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось выполнить one-time stamp зашифрованных расписаний.");
        }
    }
}
