using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using eBackup.Core.History;
using eBackup.Core.Modules;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using eBackup.Security;
using eBackup.Service;
using eBackup.Service.Handlers;
using eBackup.Service.Jobs;
using eBackup.Storage;
using Xunit;

namespace eBackup.Tests.Service;

public sealed class CustomFolderAdminTests : IDisposable
{
    private readonly string _root;

    public CustomFolderAdminTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-folders-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly CallerContext Caller = new("S-1-5-21-1", IsAdmin: false);

    private (ServiceHandlers handlers, JobManager jobs) Build()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var folders = new CustomFolderStore(Path.Combine(_root, "cfg", "custom-folders.json"));
        var storages = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        var jobs = new JobManager(new BackupRunner(_ => []), rid => new JobChannel(history, rid));
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), storages, "inst", "1.2.0", folders);
        return (handlers, jobs);
    }

    [Fact]
    public async Task Register_List_Delete_Custom_Folder()
    {
        var (handlers, jobs) = Build();
        await using var _ = jobs;
        const string path = @"D:\Data\Project";

        await handlers.UpsertCustomFolderAsync(new UpsertCustomFolderRequest { Path = path }, Caller, default);
        await handlers.UpsertCustomFolderAsync(new UpsertCustomFolderRequest { Path = path }, Caller, default); // дубль

        var listed = await handlers.ListCustomFoldersAsync(Caller, default);
        Assert.Equal(new[] { path }, listed); // один раз, без дублей

        await handlers.DeleteCustomFolderAsync(new DeleteByIdRequest { Id = path }, Caller, default);
        Assert.Empty(await handlers.ListCustomFoldersAsync(Caller, default));
    }

    [Fact]
    public async Task Empty_Path_Is_BadRequest()
    {
        var (handlers, jobs) = Build();
        await using var _ = jobs;
        await Assert.ThrowsAsync<IpcFaultException>(
            () => handlers.UpsertCustomFolderAsync(new UpsertCustomFolderRequest { Path = "" }, Caller, default));
    }
}
