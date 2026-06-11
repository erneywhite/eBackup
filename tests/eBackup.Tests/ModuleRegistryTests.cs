using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using eBackup.Core.Modules;
using eBackup.Modules.Obs;
using Xunit;

namespace eBackup.Tests;

public class ModuleRegistryTests
{
    private sealed class FakeSource(ModuleSource kind, params ModuleDescriptor[] items) : IModuleSource
    {
        public ModuleSource Kind => kind;
        public IEnumerable<ModuleDescriptor> Discover() => items;
    }

    /// <summary>Изолированный путь файла «выключенных» — тесты не трогают реальный конфиг.</summary>
    private static string TempDisabledPath()
        => Path.Combine(Path.GetTempPath(), $"ebk-disabled-{Guid.NewGuid():N}.json");

    [Fact]
    public void BuiltIn_Module_Is_Discovered_Trusted_And_Loadable()
    {
        var reg = new ModuleRegistry([new BuiltInModuleSource([new ObsBackupModule()])], TempDisabledPath());

        var obs = reg.Discover().Single(d => d.Id == "obs");
        Assert.Equal(ModuleTrust.Trusted, obs.Trust);
        Assert.Null(obs.Problem);
        Assert.True(obs.Enabled);
        Assert.Contains(reg.LoadEnabled(), m => m.Id == "obs");
    }

    [Fact]
    public void Duplicate_Id_Is_Blocked_Not_Crash()
    {
        var dupe = new ModuleDescriptor
        {
            Id = "obs",
            DisplayName = "Fake OBS",
            Source = ModuleSource.Declarative
        };

        var reg = new ModuleRegistry(
        [
            new BuiltInModuleSource([new ObsBackupModule()]),  // приоритетнее
            new FakeSource(ModuleSource.Declarative, dupe),
        ], TempDisabledPath());

        var obs = reg.Discover().Where(d => d.Id == "obs").ToList();
        Assert.Equal(2, obs.Count);
        Assert.Contains(obs, d => d.Source == ModuleSource.BuiltIn && d.Problem is null);
        Assert.Contains(obs, d => d.Source == ModuleSource.Declarative && d.Trust == ModuleTrust.Blocked && d.Problem is not null);

        // Движок получает ровно один экземпляр obs (встроенный), без падения на дубликате.
        Assert.Single(reg.LoadEnabled(), m => m.Id == "obs");
    }

    [Fact]
    public void Disabled_Module_Excluded_From_Backup_But_Not_From_Restore()
    {
        var path = TempDisabledPath();
        try
        {
            var reg = new ModuleRegistry([new BuiltInModuleSource([new ObsBackupModule()])], path);

            reg.SetEnabled("obs", false);

            Assert.False(reg.Discover().Single(d => d.Id == "obs").Enabled);
            Assert.DoesNotContain(reg.LoadEnabled(), m => m.Id == "obs");     // в бэкап не идёт
            Assert.Contains(reg.LoadForRestore(), m => m.Id == "obs");        // restore-хуки работают

            reg.SetEnabled("obs", true);
            Assert.Contains(reg.LoadEnabled(), m => m.Id == "obs");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
