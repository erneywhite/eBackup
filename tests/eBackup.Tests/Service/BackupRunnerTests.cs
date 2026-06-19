using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.Abstractions;
using eBackup.Core.History;
using eBackup.Core.Model;
using eBackup.Ipc.Contracts;
using eBackup.Security;
using eBackup.Service.Jobs;
using eBackup.Storage;
using Xunit;

namespace eBackup.Tests.Service;

public class BackupRunnerTests : IDisposable
{
    private readonly string _root;

    public BackupRunnerTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-runner-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class DirModule(string id, string tokenPath) : IBackupModule
    {
        public string Id => id;
        public string DisplayName => id;
        public Task<IReadOnlyList<PathEntry>> DiscoverAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PathEntry>>(
                [new PathEntry { TokenPath = tokenPath, Type = PathEntryType.Directory, ArchivePath = "data" }]);
    }

    private static Job MakeJob(StartBackupRequest req, HistoryStore history)
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
            Request = req,
            Channel = new JobChannel(history, runId),
        };
    }

    [Fact]
    public async Task Builds_Local_Archive_For_Resolved_Module()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "keep.txt"), "hello");

        var buildDir = Path.Combine(_root, "build");
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var module = new DirModule("test", src.Replace('\\', '/')); // сырой абсолютный путь источника
        var runner = new BackupRunner(_ => [module], buildDir);

        var job = MakeJob(new StartBackupRequest { ModuleIds = ["test"], CompressionMode = 1 }, history);
        var outcome = await runner.RunAsync(job, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.NotNull(outcome.ArchiveName);
        Assert.True(outcome.SizeBytes > 0);
        Assert.True(File.Exists(Path.Combine(buildDir, outcome.ArchiveName!)));

        // В журнал что-то записалось (seq-строки доступны).
        Assert.NotEmpty(history.ReadLogFromSeq(job.RunId, 0, 100));
    }

    [Fact]
    public async Task Backs_Up_Custom_Folders()
    {
        var src = Path.Combine(_root, "myfolder");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "doc.txt"), "hi");

        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var buildDir = Path.Combine(_root, "build");
        // модулей нет; резолвер папок — тождественный (id == путь)
        var runner = new BackupRunner(_ => [], buildDir, resolveFolders: ids => ids.ToList());

        var job = MakeJob(new StartBackupRequest { CustomFolderIds = [src] }, history);
        var outcome = await runner.RunAsync(job, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.True(outcome.SizeBytes > 0);
        using var zip = ZipFile.OpenRead(Path.Combine(buildDir, outcome.ArchiveName!));
        Assert.Contains(zip.Entries, e => e.FullName.Contains("doc.txt"));
    }

    [Fact]
    public async Task Uploads_And_Verifies_To_Local_Folder_Storage()
    {
        var src = Path.Combine(_root, "myfolder");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "doc.txt"), "hi");

        var remote = Path.Combine(_root, "remote"); // «хранилище» — локальная папка-назначение
        Directory.CreateDirectory(remote);

        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var buildDir = Path.Combine(_root, "build");

        var store = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        await store.SaveAllAsync(
            [new SavedStorage { Id = "dest", Name = "Папка", Kind = StorageKind.LocalFolder, Path = remote }]);

        var runner = new BackupRunner(_ => [], buildDir, resolveFolders: ids => ids.ToList(), storages: store);

        var job = MakeJob(
            new StartBackupRequest { CustomFolderIds = [src], TargetStorageIds = ["dest"] }, history);
        var outcome = await runner.RunAsync(job, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.True(File.Exists(Path.Combine(remote, outcome.ArchiveName!)));   // архив залит в хранилище
        Assert.False(File.Exists(Path.Combine(buildDir, outcome.ArchiveName!))); // временную сборку убрали
    }

    [Fact]
    public async Task Backup_Leaves_No_Run_Dir_And_SweepOrphanRuns_Cleans_Crashed_Ones()
    {
        var src = Path.Combine(_root, "myfolder");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "doc.txt"), "hi");

        var buildDir = Path.Combine(_root, "build");
        Directory.CreateDirectory(buildDir);
        // Осиротевшая run-папка от «упавшего» прошлого процесса + её plaintext-хвост.
        var orphan = Path.Combine(buildDir, "run-orphandeadbeef");
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "x.ebk.plain"), "stale");

        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var runner = new BackupRunner(_ => [], buildDir, resolveFolders: ids => ids.ToList());
        var outcome = await runner.RunAsync(MakeJob(new StartBackupRequest { CustomFolderIds = [src] }, history), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.True(File.Exists(Path.Combine(buildDir, outcome.ArchiveName!)));  // локальный архив на месте
        var runs = Directory.EnumerateDirectories(buildDir, "run-*").Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { "run-orphandeadbeef" }, runs);                      // свою run-папку прогон зачистил, осталась только осиротевшая

        BackupRunner.SweepOrphanRuns(buildDir);                                  // старт службы подметает осиротевшие
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public async Task No_Modules_Fails_Gracefully()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var runner = new BackupRunner(_ => [], Path.Combine(_root, "build"));
        var outcome = await runner.RunAsync(MakeJob(new StartBackupRequest { ModuleIds = [] }, history), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Null(outcome.ArchiveName);
    }
}
