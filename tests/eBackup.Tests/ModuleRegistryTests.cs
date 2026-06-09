using System.Collections.Generic;
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

    [Fact]
    public void BuiltIn_Module_Is_Discovered_Trusted_And_Loadable()
    {
        var reg = new ModuleRegistry([new BuiltInModuleSource([new ObsBackupModule()])]);

        var obs = reg.Discover().Single(d => d.Id == "obs");
        Assert.Equal(ModuleTrust.Trusted, obs.Trust);
        Assert.Null(obs.Problem);
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
        ]);

        var obs = reg.Discover().Where(d => d.Id == "obs").ToList();
        Assert.Equal(2, obs.Count);
        Assert.Contains(obs, d => d.Source == ModuleSource.BuiltIn && d.Problem is null);
        Assert.Contains(obs, d => d.Source == ModuleSource.Declarative && d.Trust == ModuleTrust.Blocked && d.Problem is not null);

        // Движок получает ровно один экземпляр obs (встроенный), без падения на дубликате.
        Assert.Single(reg.LoadEnabled(), m => m.Id == "obs");
    }
}
