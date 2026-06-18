using System;
using System.IO;
using System.Security.Principal;
using eBackup.Service;
using Xunit;

namespace eBackup.Tests.Service;

public class UserProfilePathsTests
{
    [Fact]
    public void Resolves_Current_User_Tokens_To_That_Profile()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var p = new UserProfilePaths(sid);

        // Для ТЕКУЩЕГО пользователя per-SID резолв обязан совпасть с папками процесса.
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), p.Resolve("{APPDATA}"));
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio"),
            p.Resolve("{APPDATA}/obs-studio"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), p.Resolve("{LOCALAPPDATA}"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Resolve("{USERPROFILE}"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), p.Resolve("{PROGRAMDATA}"));
    }

    [Fact]
    public void Raw_Absolute_Path_Passes_Through()
    {
        var p = new UserProfilePaths(WindowsIdentity.GetCurrent().User!.Value);
        Assert.Equal(@"D:\Servers\mc", p.Resolve("D:/Servers/mc"));
    }

    [Fact]
    public void Unknown_Sid_Falls_Back_To_Process_Resolve()
    {
        var p = new UserProfilePaths("S-1-5-21-0-0-0-99999"); // профиля нет
        Assert.Equal(eBackup.Core.Paths.PathTokens.Resolve("{APPDATA}/x"), p.Resolve("{APPDATA}/x"));
    }
}
