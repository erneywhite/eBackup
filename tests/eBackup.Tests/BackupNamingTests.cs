using eBackup.Core.Engine;
using Xunit;

namespace eBackup.Tests;

public class BackupNamingTests
{
    [Theory]
    [InlineData("ebackup_PC_obs_2026-06-14_22-01-33.ebk", "ebackup_PC_obs")]
    [InlineData("ebackup_PC_minecraft-java_2026-06-14_22-04-53.ebk", "ebackup_PC_minecraft-java")]
    [InlineData("ebackup_obs_2026-06-10_14-05-33", "ebackup_obs")]            // без .ebk
    [InlineData("ebackup_folders_2026-06-11_19-40-01.ebk", "ebackup_folders")]
    public void RetentionGroupKey_Strips_Timestamp(string name, string expected)
        => Assert.Equal(expected, BackupNaming.RetentionGroupKey(name));

    [Fact]
    public void RetentionGroupKey_Separates_Module_Sets()
    {
        // Главное: разные наборы модулей дают разные ключи (не вытесняют друг друга).
        var obs = BackupNaming.RetentionGroupKey("ebackup_PC_obs_2026-06-14_22-01-33.ebk");
        var mc = BackupNaming.RetentionGroupKey("ebackup_PC_minecraft-java_2026-06-14_22-04-53.ebk");
        Assert.NotEqual(obs, mc);
    }

    [Fact]
    public void TryParseTimestamp_Reads_Name_Suffix()
    {
        Assert.True(BackupNaming.TryParseTimestamp("ebackup_PC_obs_2026-06-14_22-01-33.ebk", out var ts));
        Assert.Equal(new DateTime(2026, 6, 14, 22, 1, 33), ts);
        Assert.False(BackupNaming.TryParseTimestamp("notes.txt", out _));
    }

    [Fact]
    public void SelectForRetention_Orders_By_Name_Timestamp_Not_Listing_Date()
    {
        // Листинг отдаёт одинаковую/бессмысленную дату (FTP без MDTM, время заливки) —
        // удалять надо самые старые ПО ИМЕНИ, а не случайные.
        var sameDate = new DateTime(2026, 7, 1);
        var files = new[]
        {
            ("ebackup_PC_obs_2026-06-12_10-00-00.ebk", sameDate),
            ("ebackup_PC_obs_2026-06-14_10-00-00.ebk", DateTime.MinValue),
            ("ebackup_PC_obs_2026-06-10_10-00-00.ebk", sameDate.AddDays(5)),
            ("ebackup_PC_obs_2026-06-13_10-00-00.ebk", sameDate),
            ("ebackup_PC_minecraft-java_2026-01-01_10-00-00.ebk", DateTime.MinValue), // другая группа
        };

        var deleted = BackupNaming.SelectForRetention(
            files, f => f.Item1, f => f.Item2, "ebackup_PC_obs_2026-06-14_10-00-00.ebk", keep: 2);

        Assert.Equal(
            ["ebackup_PC_obs_2026-06-12_10-00-00.ebk", "ebackup_PC_obs_2026-06-10_10-00-00.ebk"],
            deleted.Select(f => f.Item1));
    }

    [Fact]
    public void SelectForRetention_Keep_Zero_Deletes_Nothing()
        => Assert.Empty(BackupNaming.SelectForRetention(
            new[] { "ebackup_obs_2026-06-12_10-00-00.ebk" }, n => n, _ => DateTime.Now,
            "ebackup_obs_2026-06-13_10-00-00.ebk", keep: 0));

    [Fact]
    public void RetentionGroupKey_Same_Set_Different_Time_Same_Key()
    {
        var a = BackupNaming.RetentionGroupKey("ebackup_PC_obs_2026-06-14_22-01-33.ebk");
        var b = BackupNaming.RetentionGroupKey("ebackup_PC_obs_2026-06-10_09-15-00.ebk");
        Assert.Equal(a, b);
    }
}
