using System;
using System.IO;
using eBackup.Core.Paths;
using Xunit;

namespace eBackup.Tests;

public class PathTokensTests
{
    [Fact]
    public void Tokenize_Then_Resolve_Roundtrips_AppData()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var original = Path.Combine(appData, "obs-studio", "global.ini");

        var token = PathTokens.Tokenize(original);
        Assert.StartsWith("{APPDATA}", token);

        var resolved = PathTokens.Resolve(token);
        Assert.Equal(Path.GetFullPath(original), Path.GetFullPath(resolved));
    }

    [Fact]
    public void Tokenize_Prefers_LocalAppData_Over_AppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var original = Path.Combine(localAppData, "SomeApp", "config.json");

        var token = PathTokens.Tokenize(original);

        Assert.StartsWith("{LOCALAPPDATA}", token);
    }
}
