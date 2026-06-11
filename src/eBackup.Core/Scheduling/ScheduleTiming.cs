namespace eBackup.Core.Scheduling;

/// <summary>Расчёт времени срабатывания расписаний (чистая логика, без таймеров и win32).</summary>
public static class ScheduleTiming
{
    /// <summary>
    /// Окно после точного времени, в котором запуск ещё считается своевременным
    /// (например, приложение было занято другим бэкапом). Пропущенные за пределами
    /// окна запуски НЕ догоняются — ждём следующего вхождения.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    /// <summary>Пора ли запускать. <paramref name="idle"/> — время без ввода пользователя.</summary>
    public static bool IsDue(BackupSchedule s, DateTime now, TimeSpan idle)
    {
        if (!s.Enabled)
            return false;

        switch (s.Kind)
        {
            case ScheduleKind.Daily:
            case ScheduleKind.Weekly:
            {
                var occurrence = MostRecentOccurrence(s, now);
                if (occurrence is null)
                    return false;
                if (s.LastRunAt is { } last && last >= occurrence.Value)
                    return false; // это вхождение уже отработано
                return now - occurrence.Value <= Grace;
            }
            case ScheduleKind.EveryHours:
                return s.LastRunAt is null
                    || now >= s.LastRunAt.Value.AddHours(Math.Max(1, s.EveryHours));

            case ScheduleKind.DailyWhenIdle:
                return (s.LastRunAt is null || s.LastRunAt.Value.Date < now.Date)
                    && idle >= TimeSpan.FromMinutes(Math.Max(1, s.IdleMinutes));

            default:
                return false;
        }
    }

    /// <summary>
    /// Ближайший БУДУЩИЙ запуск — для отображения. null: выключено либо
    /// «при простое» (момент выбирает приложение, точного времени нет).
    /// </summary>
    public static DateTime? NextRun(BackupSchedule s, DateTime now)
    {
        if (!s.Enabled)
            return null;

        switch (s.Kind)
        {
            case ScheduleKind.Daily:
            {
                var occurrence = now.Date + new TimeSpan(s.Hour, s.Minute, 0);
                if (occurrence < now)
                    occurrence = occurrence.AddDays(1);
                return occurrence;
            }
            case ScheduleKind.Weekly:
            {
                if (s.Days.Count == 0)
                    return null;
                var occurrence = now.Date + new TimeSpan(s.Hour, s.Minute, 0);
                while (!s.Days.Contains(occurrence.DayOfWeek) || occurrence < now)
                    occurrence = occurrence.AddDays(1);
                return occurrence;
            }
            case ScheduleKind.EveryHours:
                return (s.LastRunAt ?? now).AddHours(Math.Max(1, s.EveryHours));

            default:
                return null;
        }
    }

    /// <summary>Последнее вхождение точного времени, не позже <paramref name="now"/>.</summary>
    private static DateTime? MostRecentOccurrence(BackupSchedule s, DateTime now)
    {
        var time = new TimeSpan(s.Hour, s.Minute, 0);
        switch (s.Kind)
        {
            case ScheduleKind.Daily:
            {
                var occurrence = now.Date + time;
                if (occurrence > now)
                    occurrence = occurrence.AddDays(-1);
                return occurrence;
            }
            case ScheduleKind.Weekly:
            {
                if (s.Days.Count == 0)
                    return null;
                var occurrence = now.Date + time;
                while (!s.Days.Contains(occurrence.DayOfWeek) || occurrence > now)
                    occurrence = occurrence.AddDays(-1);
                return occurrence;
            }
            default:
                return null;
        }
    }
}
