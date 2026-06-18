using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using eBackup.Core.History;
using eBackup.Core.Modules;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using eBackup.Security;
using eBackup.Service.Handlers;
using eBackup.Service.Jobs;
using eBackup.Storage;
using Xunit;

namespace eBackup.Tests.Service;

public sealed class ModuleAdminTests : IDisposable
{
    private readonly string _root;

    public ModuleAdminTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-modadmin-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly CallerContext Caller = new("S-1-5-21-1", IsAdmin: false);

    [Fact]
    public async Task Install_List_Discover_Delete_Declarative_Module()
    {
        var modulesDir = Path.Combine(_root, "modules");
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var registry = new ModuleRegistry(
            [new DeclarativeModuleSource(modulesDir)],
            disabledConfigPath: Path.Combine(_root, "disabled.json"));
        var storages = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        await using var jobs = new JobManager(new BackupRunner(_ => []), rid => new JobChannel(history, rid));
        var handlers = new ServiceHandlers(jobs, history, registry, storages, "inst", "1.2.0", modulesDir: modulesDir);

        const string json = """
            {"id":"testmod","displayName":"Тест-модуль","entries":[{"tokenPath":"{APPDATA}/Test","type":"Directory","archivePath":"test"}]}
            """;
        await handlers.InstallModuleAsync(new InstallModuleRequest { DeclarativeJson = json }, Caller, default);

        var mod = (await handlers.ListModulesAsync(Caller, default)).Single(m => m.Id == "testmod");
        Assert.Equal("Тест-модуль", mod.DisplayName);
        Assert.Equal("Declarative", mod.Source);

        var entries = await handlers.DiscoverModuleAsync(new DiscoverModuleRequest { Id = "testmod" }, Caller, default);
        Assert.Contains(entries, e => e.TokenPath == "{APPDATA}/Test" && e.Type == "Directory");

        await handlers.DeleteModuleAsync(new DeleteByIdRequest { Id = "testmod" }, Caller, default);
        Assert.DoesNotContain(await handlers.ListModulesAsync(Caller, default), m => m.Id == "testmod");

        await Assert.ThrowsAsync<IpcFaultException>(
            () => handlers.InstallModuleAsync(new InstallModuleRequest { DeclarativeJson = "{ not json" }, Caller, default));
    }
}
