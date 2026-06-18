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
using eBackup.Service.Jobs;
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
    public async Task No_Modules_Fails_Gracefully()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var runner = new BackupRunner(_ => [], Path.Combine(_root, "build"));
        var outcome = await runner.RunAsync(MakeJob(new StartBackupRequest { ModuleIds = [] }, history), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Null(outcome.ArchiveName);
    }
}
