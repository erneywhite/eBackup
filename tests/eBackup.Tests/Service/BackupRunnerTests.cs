using System;
using System.Collections.Generic;
using System.IO;
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

    private static Job MakeJob(StartBackupRequest req) => new()
    {
        Seq = 1,
        JobId = "j1",
        RunId = "run-" + Guid.NewGuid().ToString("N")[..8],
        OwnerSid = "S-1-5-21-1",
        Trigger = "тест",
        Origin = "Interactive",
        Request = req,
    };

    [Fact]
    public async Task Builds_Local_Archive_For_Resolved_Module()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "keep.txt"), "hello");

        var buildDir = Path.Combine(_root, "build");
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var module = new DirModule("test", src.Replace('\\', '/')); // сырой абсолютный путь источника
        var runner = new BackupRunner(_ => [module], history, buildDir);

        var job = MakeJob(new StartBackupRequest { ModuleIds = ["test"], CompressionMode = 1 });
        var outcome = await runner.RunAsync(job, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.NotNull(outcome.ArchiveName);
        Assert.True(outcome.SizeBytes > 0);
        Assert.True(File.Exists(Path.Combine(buildDir, outcome.ArchiveName!)));

        // В журнал что-то записалось (seq-строки доступны).
        Assert.NotEmpty(history.ReadLogFromSeq(job.RunId, 0, 100));
    }

    [Fact]
    public async Task No_Modules_Fails_Gracefully()
    {
        var runner = new BackupRunner(_ => [], new HistoryStore(Path.Combine(_root, "hist")), Path.Combine(_root, "build"));
        var outcome = await runner.RunAsync(MakeJob(new StartBackupRequest { ModuleIds = [] }), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Null(outcome.ArchiveName);
    }
}
