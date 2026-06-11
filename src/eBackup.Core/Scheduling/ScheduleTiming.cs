namespace eBackup.Core.Scheduling;

/// <summary>Расчёт времени срабатывания расписаний (чистая логика, без таймеров).</summary>
public static class ScheduleTiming
{
    /// <summary>
    /// Ближайший запуск после <see cref="BackupSchedule.LastRunAt"/>.
    /// Может быть в прошлом относительно <paramref name="now"/> — тогда расписание
    /// «просрочено» и подлежит запуску (догон после простоя приложения).
    /// null — расписание выключено.
    /// </summary>
    public static DateTime? NextRun(BackupSchedule s, DateTime now)
    {
        if (!s.Enabled)
            return null;

        switch (s.Kind)
        {
            case ScheduleKind.Daily:
            {
                // Первое вхождение HH:MM строго после LastRunAt (или ближайшее к now, если запусков не было).
                var anchor = s.LastRunAt ?? now.AddDays(-1);
                var occurrence = anchor.Date + new TimeSpan(s.Hour, s.Minute, 0);
                while (occurrence <= anchor)
                    occurrence = occurrence.AddDays(1);
                return occurrence;
            }
            case ScheduleKind.Weekly:
            {
                var anchor = s.LastRunAt ?? now.AddDays(-7);
                var occurrence = anchor.Date + new TimeSpan(s.Hour, s.Minute, 0);
                while (occurrence.DayOfWeek != s.Day || occurrence <= anchor)
                    occurrence = occurrence.AddDays(1);
                return occurrence;
            }
            case ScheduleKind.EveryHours:
            {
                var hours = Math.Max(1, s.EveryHours);
                return (s.LastRunAt ?? now) + TimeSpan.FromHours(s.LastRunAt is null ? 0 : hours);
            }
            default:
                return null;
        }
    }

    /// <summary>Пора ли запускать.</summary>
    public static bool IsDue(BackupSchedule s, DateTime now)
        => NextRun(s, now) is { } next && next <= now;
}
