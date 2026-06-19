using System;
using System.Linq;
using System.Text;
using eBackup.Core.Scheduling;
using eBackup.Core.Security;
using eBackup.Ipc.Contracts;
using eBackup.Service.Handlers;
using Xunit;

namespace eBackup.Tests;

public class ScheduleInputMapperTests
{
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["enc:".Length..]));
    }

    private static readonly ScheduleStore Store = new(new FakeProtector(), "ignored.json");

    private static ScheduleInput Input() => new()
    {
        Id = "s1",
        Name = "Ночной",
        Kind = "Weekly",
        ModuleIds = ["obs"],
        CustomFolderIds = [@"D:\proj"],
        TargetStorageIds = ["NAS"],
        Days = [1, 5, 1], // пн, пт, дубль пн
        Hour = 2,
        Minute = 30,
    };

    [Fact]
    public void Maps_Fields_And_Dedups_Days()
    {
        var s = ScheduleInputMapper.ToSchedule(Input(), Store, existing: null, ownerSid: "S-1-5-21-USER");

        Assert.Equal("s1", s.Id);
        Assert.Equal("S-1-5-21-USER", s.OwnerSid);            // владелец нового — вызывающий
        Assert.Equal(ScheduleKind.Weekly, s.Kind);
        Assert.Equal(["obs"], s.ModuleIds);
        Assert.Equal([@"D:\proj"], s.CustomFolders);          // CustomFolderIds → CustomFolders
        Assert.Equal(["NAS"], s.TargetConnectionIds);         // TargetStorageIds → TargetConnectionIds
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], s.Days); // дубликат пн схлопнут
        Assert.Null(s.LastRunAt);                              // штамп «сейчас» ставит хендлер
    }

    [Fact]
    public void NewPassphrase_Set_Encrypts_With_Machine_Key()
    {
        var s = ScheduleInputMapper.ToSchedule(Input() with { NewPassphrase = "hunter2" }, Store, null, "sid");
        Assert.Equal("enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("hunter2")), s.ProtectedPassphrase);
    }

    [Fact]
    public void NewPassphrase_Null_Keeps_Existing_And_Owner_And_LastRun()
    {
        var existing = new BackupSchedule
        {
            Id = "s1", Name = "old", OwnerSid = "S-1-5-21-OWNER",
            ProtectedPassphrase = "enc:KEEP", LastRunAt = new DateTime(2026, 6, 1, 3, 0, 0),
        };

        var s = ScheduleInputMapper.ToSchedule(Input() with { NewPassphrase = null }, Store, existing, ownerSid: "S-1-5-21-OTHER");

        Assert.Equal("enc:KEEP", s.ProtectedPassphrase);            // фраза сохранена
        Assert.Equal("S-1-5-21-OWNER", s.OwnerSid);                 // владелец при правке не меняется
        Assert.Equal(new DateTime(2026, 6, 1, 3, 0, 0), s.LastRunAt); // LastRunAt сохранён
    }

    [Fact]
    public void NewPassphrase_Empty_Clears_Encryption()
    {
        var existing = new BackupSchedule { Id = "s1", Name = "old", ProtectedPassphrase = "enc:KEEP" };
        var s = ScheduleInputMapper.ToSchedule(Input() with { NewPassphrase = "" }, Store, existing, "sid");
        Assert.Null(s.ProtectedPassphrase);
    }

    [Fact]
    public void Detail_Reports_Passphrase_Presence_NextRun_And_Editable_Fields()
    {
        var s = new BackupSchedule
        {
            Id = "s1", Name = "Ежедневный", Kind = ScheduleKind.Daily, Hour = 3, Minute = 0,
            ProtectedPassphrase = "enc:x", OwnerSid = "sid",
            ModuleIds = ["obs"], CustomFolders = [@"D:\p"], TargetConnectionIds = ["NAS"],
            Days = [DayOfWeek.Monday, DayOfWeek.Friday],
            LastRunAt = new DateTime(2026, 6, 18, 3, 0, 0),
        };
        var now = new DateTime(2026, 6, 19, 10, 0, 0);

        var dto = ScheduleInputMapper.ToDetail(s, now);

        Assert.True(dto.HasPassphrase);
        Assert.Equal("sid", dto.OwnerSid);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 6, 20, 3, 0, 0)), dto.NextRun); // завтра 03:00
        Assert.Equal(["obs"], dto.ModuleIds);
        Assert.Equal([@"D:\p"], dto.CustomFolderIds);
        Assert.Equal(["NAS"], dto.TargetStorageIds);
        Assert.Equal([(int)DayOfWeek.Monday, (int)DayOfWeek.Friday], dto.Days); // DayOfWeek → int
    }
}
