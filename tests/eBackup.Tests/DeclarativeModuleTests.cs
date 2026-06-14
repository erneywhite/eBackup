using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using eBackup.Core.Engine;
using eBackup.Core.Modules;
using Xunit;

namespace eBackup.Tests;

public class DeclarativeModuleTests
{
    private static string WriteDescriptor(string dir, string fileName, string json)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task Valid_Descriptor_Loads_Trusted_With_Prefixed_Path()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ebk-mods-{Guid.NewGuid():N}");
        try
        {
            WriteDescriptor(dir, "app.module.json", """
                {
                  "id": "myapp",
                  "displayName": "My App",
                  "entries": [
                    { "tokenPath": "{APPDATA}/MyApp", "type": "Directory", "archivePath": "config", "excludeGlobs": ["**/Cache/**"] }
                  ]
                }
                """);

            var desc = new DeclarativeModuleSource(dir).Discover().Single();
            Assert.Equal("myapp", desc.Id);
            Assert.Equal(ModuleTrust.Trusted, desc.Trust);
            Assert.Null(desc.Problem);

            var entries = await desc.Instance!.DiscoverAsync();
            var entry = entries.Single();
            Assert.Equal("myapp/config", entry.ArchivePath);     // под data/<id>/
            Assert.False(entry.ManagedByModule);                 // декларативный никогда не управляет restore
            Assert.Contains("**/Cache/**", entry.ExcludeGlobs);      // свои маски сохраняются
            Assert.DoesNotContain("**/.ssh/**", entry.ExcludeGlobs); // форс-денлиста больше нет
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("{APPDATA}/../x")]            // обход вверх
    [InlineData("{LOCALAPPDATA}/a/../../b")]  // обход в середине
    public void Traversal_Path_Is_Blocked(string tokenPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ebk-mods-{Guid.NewGuid():N}");
        try
        {
            WriteDescriptor(dir, "x.module.json", $$"""
                { "id": "x", "entries": [ { "tokenPath": "{{tokenPath}}", "type": "Directory", "archivePath": "d" } ] }
                """);

            var desc = new DeclarativeModuleSource(dir).Discover().Single();
            Assert.Equal(ModuleTrust.Blocked, desc.Trust);
            Assert.NotNull(desc.Problem);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("{USERPROFILE}/curseforge/minecraft/Instances")]  // игровой лаунчер вне app-data
    [InlineData("{USERPROFILE}/.ssh")]                            // ключи — бэкап-тул вправе
    [InlineData("{PROGRAMFILES}/MyGameServer")]                   // вне app-data
    [InlineData("C:/Servers/mc")]                                 // raw absolute (личный модуль)
    public void Any_NonTraversal_Path_Is_Allowed(string tokenPath)
    {
        // eBackup — бэкап-тул: модуль вправе собирать любой путь без «..».
        var dir = Path.Combine(Path.GetTempPath(), $"ebk-mods-{Guid.NewGuid():N}");
        try
        {
            WriteDescriptor(dir, "g.module.json", $$"""
                { "id": "g", "entries": [ { "tokenPath": "{{tokenPath}}", "type": "Directory", "archivePath": "d" } ] }
                """);

            var desc = new DeclarativeModuleSource(dir).Discover().Single();
            Assert.Equal(ModuleTrust.Trusted, desc.Trust);
            Assert.Null(desc.Problem);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Newer_Required_Api_Is_Blocked()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ebk-mods-{Guid.NewGuid():N}");
        try
        {
            WriteDescriptor(dir, "future.module.json", """
                { "id": "future", "minApiVersion": "2.0", "entries": [ { "tokenPath": "{APPDATA}/F", "type": "Directory", "archivePath": "d" } ] }
                """);

            var desc = new DeclarativeModuleSource(dir).Discover().Single();
            Assert.Equal(ModuleTrust.Blocked, desc.Trust);
            Assert.Contains("новее", desc.Problem);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Declarative_Module_Backs_Up_And_Restores_Through_Engine()
    {
        var modsDir = Path.Combine(Path.GetTempPath(), $"ebk-mods-{Guid.NewGuid():N}");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var srcName = $"eBackup-decltest-{Guid.NewGuid():N}";
        var srcDir = Path.Combine(localAppData, srcName);
        var outDir = Path.Combine(Path.GetTempPath(), $"ebk-out-{Guid.NewGuid():N}");
        var restoreDir = Path.Combine(Path.GetTempPath(), $"ebk-restore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(srcDir);
            await File.WriteAllTextAsync(Path.Combine(srcDir, "settings.json"), "{}");

            WriteDescriptor(modsDir, "tst.module.json", $$"""
                { "id": "tst", "displayName": "T", "entries": [ { "tokenPath": "{LOCALAPPDATA}/{{srcName}}", "type": "Directory", "archivePath": "cfg" } ] }
                """);

            var module = new DeclarativeModuleSource(modsDir).Discover().Single().Instance!;
            var engine = new BackupEngine();
            var archive = await engine.CreateBackupAsync([module], outDir, "decl");
            await engine.RestoreAsync(archive, destinationRootOverride: restoreDir);

            var restored = Path.Combine(restoreDir, "tst", "cfg", "settings.json");
            Assert.True(File.Exists(restored));
        }
        finally
        {
            foreach (var d in new[] { modsDir, srcDir, outDir, restoreDir })
                if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        }
    }
}
