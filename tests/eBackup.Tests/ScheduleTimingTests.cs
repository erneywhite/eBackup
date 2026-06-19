using System;
using System.Collections.Generic;
using System.Text.Json;
using eBackup.Core.Model;
using eBackup.Core.Scheduling;
using Xunit;

namespace eBackup.Tests;

public class ScheduleTimingTests
{
    private static readonly TimeSpan NoIdle = TimeSpan.Zero;

    [Fact]
    public void Old_Schedule_Json_Deserializes_With_New_Field_Defaults()
    {
        // Старый schedules.json (без полей S6) должен читаться с дефолтами (back-compat).
        const string oldJson = """[{"id":"x","name":"Тест","kind":"Daily","hour":3,"enabled":true}]""";
        var list = JsonSerializer.Deserialize<List<BackupSchedule>>(oldJson, ManifestJson.Options);
        var s = Assert.Single(list!);
        Assert.Equal("x", s.Id);
        Assert.Null(s.OwnerSid);
        Assert.Equal(0, s.CompressionMode);
        Assert.False(s.IncludeMachineName);
        Assert.Null(s.RetentionCount);
    }

    private static BackupSchedule Make(ScheduleKind kind) => new() { Id = "s1", Name = "test", Kind = kind };

    [Fact]
    public void Daily_Fires_On_Time_But_Missed_Runs_Are_Not_Caught_Up()
    {
        var s = Make(ScheduleKind.Daily) with { Hour = 3, Minute = 0, LastRunAt = new DateTime(2026, 6, 10, 3, 0, 0) };

        // Вовремя (и в пределах окна допуска) — пора.
        Assert.True(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 11, 3, 0, 30), NoIdle));
        Assert.True(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 11, 3, 25, 0), NoIdle));

        // Комп был выключен в 03:00, включили в 09:00 — НЕ догоняем.
        Assert.False(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 11, 9, 0, 0), NoIdle));

        // Отображение: следующий запуск — завтрашние 03:00.
        Assert.Equal(new DateTime(2026, 6, 12, 3, 0, 0),
            ScheduleTiming.NextRun(s, new DateTime(2026, 6, 11, 9, 0, 0)));
    }

    [Fact]
    public void Daily_Does_Not_Fire_Twice_For_Same_Occurrence()
    {
        var s = Make(ScheduleKind.Daily) with { Hour = 3, Minute = 0, LastRunAt = new DateTime(2026, 6, 11, 3, 1, 0) };

        Assert.False(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 11, 3, 10, 0), NoIdle));
    }

    [Fact]
    public void Weekly_Fires_Only_On_Selected_Day_Within_Grace()
    {
        // 2026-06-15 — понедельник.
        var s = Make(ScheduleKind.Weekly) with
        {
            Days = [DayOfWeek.Monday],
            Hour = 12,
            Minute = 0,
            LastRunAt = new DateTime(2026, 6, 8, 12, 0, 0)
        };

        Assert.True(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 15, 12, 5, 0), NoIdle));
        // Опоздали на 3 часа — пропуск, ждём следующий понедельник.
        Assert.False(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 15, 15, 0, 0), NoIdle));
        Assert.Equal(new DateTime(2026, 6, 22, 12, 0, 0),
            ScheduleTiming.NextRun(s, new DateTime(2026, 6, 15, 15, 0, 0)));
    }

    [Fact]
    public void Weekly_Supports_Multiple_Days()
    {
        // Пн и пт. 2026-06-19 — пятница.
        var s = Make(ScheduleKind.Weekly) with
        {
            Days = [DayOfWeek.Monday, DayOfWeek.Friday],
            Hour = 12,
            Minute = 0,
            LastRunAt = new DateTime(2026, 6, 15, 12, 0, 0) // понедельник отработал
        };

        Assert.True(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 19, 12, 10, 0), NoIdle)); // пятница
        // После пятницы следующий — понедельник.
        var ran = s with { LastRunAt = new DateTime(2026, 6, 19, 12, 10, 0) };
        Assert.Equal(new DateTime(2026, 6, 22, 12, 0, 0),
            ScheduleTiming.NextRun(ran, new DateTime(2026, 6, 19, 13, 0, 0)));
    }

    [Fact]
    public void EveryHours_Fires_After_Interval()
    {
        var s = Make(ScheduleKind.EveryHours) with
        {
            EveryHours = 6,
            LastRunAt = new DateTime(2026, 6, 10, 10, 0, 0)
        };

        Assert.False(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 10, 15, 59, 0), NoIdle));
        Assert.True(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 10, 16, 0, 0), NoIdle));
    }

    [Fact]
    public void DailyWhenIdle_Fires_Once_Per_Day_Only_When_Idle()
    {
        var s = Make(ScheduleKind.DailyWhenIdle) with { IdleMinutes = 5, LastRunAt = new DateTime(2026, 6, 10, 14, 0, 0) };
        var now = new DateTime(2026, 6, 11, 10, 0, 0);

        // Пользователь за компом — не запускаем.
        Assert.False(ScheduleTiming.IsDue(s, now, TimeSpan.FromMinutes(1)));
        // ПК в простое достаточно долго — пора.
        Assert.True(ScheduleTiming.IsDue(s, now, TimeSpan.FromMinutes(6)));
        // Сегодня уже выполнялся — больше не запускаем, даже в простое.
        var ranToday = s with { LastRunAt = new DateTime(2026, 6, 11, 9, 0, 0) };
        Assert.False(ScheduleTiming.IsDue(ranToday, now, TimeSpan.FromMinutes(60)));
        // Точного времени у режима нет.
        Assert.Null(ScheduleTiming.NextRun(s, now));
    }

    [Fact]
    public void Disabled_Schedule_Never_Due()
    {
        var s = Make(ScheduleKind.Daily) with { Enabled = false, LastRunAt = new DateTime(2026, 1, 1) };

        Assert.Null(ScheduleTiming.NextRun(s, new DateTime(2026, 6, 10)));
        Assert.False(ScheduleTiming.IsDue(s, new DateTime(2026, 6, 10), TimeSpan.FromHours(1)));
    }
}
