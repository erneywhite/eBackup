using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.History;
using eBackup.Ipc.Contracts;
using eBackup.Security;
using eBackup.Service.Jobs;
using eBackup.Storage;
using Xunit;

namespace eBackup.Tests.Service;

public sealed class RestoreRunnerTests : IDisposable
{
    private readonly string _root;

    public RestoreRunnerTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-restore-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private Job MakeJob(JobKind kind, StartBackupRequest? backup, StartRestoreRequest? restore, HistoryStore history)
    {
        var runId = "run-" + Guid.NewGuid().ToString("N")[..8];
        return new Job
        {
            Seq = 1,
            JobId = "j1",
            RunId = runId,
            OwnerSid = "S-1-5-21-1",
            Trigger = "тест",
            Origin = "Interactive",
            Kind = kind,
            Request = backup,
            Restore = restore,
            Channel = new JobChannel(history, runId),
        };
    }

    [Fact]
    public async Task Restores_Archive_From_Storage_To_Folder()
    {
        var src = Path.Combine(_root, "myfolder");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "doc.txt"), "hi");

        var remote = Path.Combine(_root, "remote");
        Directory.CreateDirectory(remote);

        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var store = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        await store.SaveAllAsync(
            [new SavedStorage { Id = "dest", Name = "D", Kind = StorageKind.LocalFolder, Path = remote }]);

        // 1) бэкап «своей папки» в хранилище
        var backup = new BackupRunner(_ => [], Path.Combine(_root, "build"),
            resolveFolders: ids => ids.ToList(), storages: store);
        var bout = await backup.RunAsync(
            MakeJob(JobKind.Backup, new StartBackupRequest { CustomFolderIds = [src], TargetStorageIds = ["dest"] }, null, history),
            CancellationToken.None);
        Assert.True(bout.Success);

        // 2) восстановление через RestoreRunner в указанную папку
        var restoreDir = Path.Combine(_root, "restored");
        var restore = new RestoreRunner(store, () => [], Path.Combine(_root, "rbuild"));
        var rout = await restore.RunAsync(
            MakeJob(JobKind.Restore, null,
                new StartRestoreRequest { SourceStorageId = "dest", RemoteName = bout.ArchiveName!, TargetDir = restoreDir, Policy = "Overwrite" },
                history),
            CancellationToken.None);

        Assert.True(rout.Success);
        Assert.True(Directory.EnumerateFiles(restoreDir, "doc.txt", SearchOption.AllDirectories).Any());
    }
}
