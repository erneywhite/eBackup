using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

/// <summary>S7a — фундамент шифрования: слот фразы переживает постановку, fail-closed на уровне типа,
/// фраза реально доходит до движка (архив зашифрован) и зануляется после прогона.</summary>
public sealed class S7aEncryptionFoundationTests : IDisposable
{
    private readonly string _root;

    public S7aEncryptionFoundationTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-s7a-{Guid.NewGuid():N}");

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

    /// <summary>Фейк, ловящий поставленную задачу (для проверки, что слот фразы пережил Submit).</summary>
    private sealed class CapturingRunner : IJobRunner
    {
        public Job? Captured;
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Done => _done.Task;
        public Task<JobOutcome> RunAsync(Job job, CancellationToken ct)
        {
            Captured = job;
            _done.TrySetResult();
            return Task.FromResult(new JobOutcome(true, 0, 0, null, null));
        }
    }

    private static Job MakeBackupJob(StartBackupRequest req, HistoryStore history, byte[]? passphrase, bool requires)
    {
        var runId = "run-" + Guid.NewGuid().ToString("N")[..8];
        return new Job
        {
            Seq = 1, JobId = "j1", RunId = runId, OwnerSid = "S-1-5-21-1",
            Trigger = "тест", Origin = "Interactive", Request = req,
            ResolvedPassphrase = passphrase, RequiresEncryption = requires,
            Channel = new JobChannel(history, runId),
        };
    }

    [Fact]
    public async Task Passphrase_Slot_And_Flag_Survive_Enqueue_Submit()
    {
        // Регресс на критическую находку: JobManager.Submit раньше пересоздавал Job по whitelist
        // и молча терял бы новый слот фразы (fail-open в открытый архив).
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var runner = new CapturingRunner();
        await using var jobs = new JobManager(runner, rid => new JobChannel(history, rid));
        var pw = Encoding.UTF8.GetBytes("secret-phrase");

        jobs.Enqueue(new StartBackupRequest { ModuleIds = ["x"] }, "S-1-5-21-1",
            resolvedPassphrase: pw, requiresEncryption: true);

        await runner.Done.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(runner.Captured);
        Assert.True(runner.Captured!.RequiresEncryption);
        Assert.Equal(pw, runner.Captured.ResolvedPassphrase); // дошёл нетронутым
    }

    [Fact]
    public async Task FailClosed_Backup_When_Encryption_Required_But_No_Passphrase()
    {
        var buildDir = Path.Combine(_root, "build");
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var runner = new BackupRunner(_ => [new DirModule("test", _root.Replace('\\', '/'))], buildDir);

        var job = MakeBackupJob(new StartBackupRequest { ModuleIds = ["test"] }, history,
            passphrase: null, requires: true);
        var outcome = await runner.RunAsync(job, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("ифрован", outcome.Error); // движок не звался, открытый архив не отдан
        Assert.False(Directory.Exists(buildDir) && Directory.EnumerateFiles(buildDir, "*.ebk*").Any());
    }

    [Fact]
    public async Task FailClosed_Restore_When_Encryption_Required_But_No_Passphrase()
    {
        var storages = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        var runner = new RestoreRunner(storages, () => [], Path.Combine(_root, "restore"));
        var runId = "run-r";
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var job = new Job
        {
            Seq = 1, JobId = "jr", RunId = runId, OwnerSid = "S-1-5-21-1",
            Trigger = "тест", Origin = "Interactive", Kind = JobKind.Restore,
            Restore = new StartRestoreRequest { SourceStorageId = "nas", RemoteName = "a.ebk" },
            RequiresEncryption = true, ResolvedPassphrase = null,
            Channel = new JobChannel(history, runId),
        };

        var outcome = await runner.RunAsync(job, CancellationToken.None);
        Assert.False(outcome.Success); // отказ ДО обращения к хранилищу (которого нет)
        Assert.Contains("ифрован", outcome.Error);
    }

    [Fact]
    public async Task Passphrase_Reaches_Engine_Encrypts_Archive_And_Is_Zeroed()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "keep.txt"), "hello");

        var buildDir = Path.Combine(_root, "build");
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var runner = new BackupRunner(_ => [new DirModule("test", src.Replace('\\', '/'))], buildDir);

        var pw = Encoding.UTF8.GetBytes("correct horse");
        var job = MakeBackupJob(new StartBackupRequest { ModuleIds = ["test"], CompressionMode = 1 }, history,
            passphrase: pw, requires: true);

        var outcome = await runner.RunAsync(job, CancellationToken.None);

        Assert.True(outcome.Success);
        var archive = Path.Combine(buildDir, outcome.ArchiveName!);
        Assert.True(File.Exists(archive));
        // Фраза дошла до движка → архив зашифрован (заголовок EBKE), а не открытый ZIP (PK).
        var head = new byte[4];
        using (var fs = File.OpenRead(archive)) { _ = fs.Read(head, 0, 4); }
        Assert.Equal("EBKE", Encoding.ASCII.GetString(head));
        // byte[] фразы занулён в finally раннера.
        Assert.All(job.ResolvedPassphrase!, b => Assert.Equal(0, b));
    }
}
