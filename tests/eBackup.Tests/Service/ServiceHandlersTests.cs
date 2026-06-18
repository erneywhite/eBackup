using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.Abstractions;
using eBackup.Core.History;
using eBackup.Core.Model;
using eBackup.Core.Modules;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using eBackup.Service.Handlers;
using eBackup.Service.Jobs;
using Xunit;

namespace eBackup.Tests.Service;

public class ServiceHandlersTests : IDisposable
{
    private readonly string _root;

    public ServiceHandlersTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-svc-{Guid.NewGuid():N}");

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

    private static readonly CallerContext Caller = new("S-1-5-21-1", IsAdmin: false);

    [Fact]
    public async Task Full_Stack_Backup_Through_Handlers()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "f.txt"), "data");

        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var module = new DirModule("test", src.Replace('\\', '/'));
        var runner = new BackupRunner(_ => [module], Path.Combine(_root, "build"));
        var writer = new JobHistoryWriter(history);
        await using var jobs = new JobManager(runner, runId => new JobChannel(history, runId), writer.OnStateChanged);
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), "inst", "1.2.0");

        var start = await handlers.StartBackupAsync(
            new StartBackupRequest { ModuleIds = ["test"], CompressionMode = 1, Trigger = "тест" }, Caller, default);
        Assert.NotEmpty(start.JobId);

        // ждём терминального состояния через GetJob (как делал бы GUI-клиент)
        JobStatus status = null!;
        var sw = Stopwatch.StartNew();
        do
        {
            await Task.Delay(20);
            status = await handlers.GetJobAsync(new GetJobRequest { JobId = start.JobId }, Caller, default);
        }
        while (status.State is "Queued" or "Running" && sw.ElapsedMilliseconds < 8000);

        Assert.Equal("Completed", status.State);
        Assert.True(status.SizeBytes > 0);
        Assert.NotNull(status.ArchiveName);

        // история запиналась (персист fire-and-forget — ждём появления записи)
        sw.Restart();
        BackupRunRecordDto[] hist;
        do
        {
            await Task.Delay(20);
            hist = await handlers.ListHistoryAsync(new ListHistoryRequest { Limit = 50 }, Caller, default);
        }
        while (!hist.Any(r => r.Id == start.RunId && r.State == "Completed") && sw.ElapsedMilliseconds < 8000);
        Assert.Contains(hist, r => r.Id == start.RunId && r.State == "Completed");

        // лог запуска читается по seq
        var log = await handlers.GetRunLogAsync(
            new GetRunLogRequest { RunId = start.RunId, FromSeq = 0, MaxLines = 100 }, Caller, default);
        Assert.NotEmpty(log.Lines);
    }

    [Fact]
    public async Task GetJob_Unknown_Throws_NotFound()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        await using var jobs = new JobManager(new BackupRunner(_ => [], Path.Combine(_root, "build")), runId => new JobChannel(history, runId));
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), "inst", "1.2.0");

        var ex = await Assert.ThrowsAsync<IpcFaultException>(
            () => handlers.GetJobAsync(new GetJobRequest { JobId = "nope" }, Caller, default));
        Assert.Equal(IpcErrorCodes.NotFound, ex.Code);
    }
}
