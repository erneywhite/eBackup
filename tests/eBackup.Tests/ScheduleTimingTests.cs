using System;
using eBackup.Core.Scheduling;
using Xunit;

namespace eBackup.Tests;

public class ScheduleTimingTests
{
    private static BackupSchedule Make(ScheduleKind kind, Action<Action<BackupSchedule>>? _ = null)
        => new() { Id = "s1", Name = "test", Kind = kind };

    [Fact]
    public void Daily_Fires_At_Next_Occurrence_After_LastRun()
    {
        var s = Make(ScheduleKind.Daily) with { Hour = 3, Minute = 0, LastRunAt = new DateTime(2026, 6, 10, 3, 0, 0) };

        // До 03:00 следующего дня — не пора.
        Assert.False(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 10, 23, 0, 0)));
        // После — пора (и «догон», если приложение было выключено в 03:00).
        Assert.True(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 11, 3, 0, 1)));
        Assert.True(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 11, 9, 30, 0)));

        Assert.Equal(new DateTime(2026, 6, 11, 3, 0, 0), ScheduleTiming.NextRun(s, new DateTime(2026, 6, 10, 23, 0, 0)));
    }

    [Fact]
    public void Weekly_Fires_On_Selected_Day()
    {
        // 2026-06-10 — среда. Расписание: по понедельникам в 12:00, последний запуск — пн 8 июня.
        var s = Make(ScheduleKind.Weekly) with
        {
            Day = DayOfWeek.Monday,
            Hour = 12,
            Minute = 0,
            LastRunAt = new DateTime(2026, 6, 8, 12, 0, 0)
        };

        Assert.False(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 10, 12, 0, 0))); // среда — рано
        Assert.Equal(new DateTime(2026, 6, 15, 12, 0, 0), ScheduleTiming.NextRun(s, new DateTime(2026, 6, 10, 12, 0, 0)));
        Assert.True(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 15, 12, 0, 1)));
    }

    [Fact]
    public void EveryHours_Fires_After_Interval()
    {
        var s = Make(ScheduleKind.EveryHours) with
        {
            EveryHours = 6,
            LastRunAt = new DateTime(2026, 6, 10, 10, 0, 0)
        };

        Assert.False(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 10, 15, 59, 0)));
        Assert.True(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 10, 16, 0, 0)));
    }

    [Fact]
    public void Disabled_Schedule_Never_Due()
    {
        var s = Make(ScheduleKind.Daily) with { Enabled = false, LastRunAt = new DateTime(2026, 1, 1) };

        Assert.Null(ScheduleTiming.NextRun(s, new DateTime(2026, 6, 10)));
        Assert.False(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 10)));
    }
}
