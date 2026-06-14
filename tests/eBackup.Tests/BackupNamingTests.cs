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
    public void RetentionGroupKey_Same_Set_Different_Time_Same_Key()
    {
        var a = BackupNaming.RetentionGroupKey("ebackup_PC_obs_2026-06-14_22-01-33.ebk");
        var b = BackupNaming.RetentionGroupKey("ebackup_PC_obs_2026-06-10_09-15-00.ebk");
        Assert.Equal(a, b);
    }
}
