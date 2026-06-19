using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.Crypto;
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

/// <summary>S7d/S7e — интерактивное шифрование end-to-end через службу: проба фразы, зашифр. круг
/// бэкап→restore, отказ при неверной фразе, резолв тикета в хендлере (fail-closed).</summary>
public sealed class S7deEncryptionTests : IDisposable
{
    private readonly string _root;
    private static readonly CallerContext Caller = new("S-1-5-21-1", IsAdmin: false);

    public S7deEncryptionTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-s7de-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

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

    private Job MakeJob(JobKind kind, StartBackupRequest? backup, StartRestoreRequest? restore,
        HistoryStore history, byte[]? passphrase, bool requires)
    {
        var runId = "run-" + Guid.NewGuid().ToString("N")[..8];
        return new Job
        {
            Seq = 1, JobId = "j1", RunId = runId, OwnerSid = "S-1-5-21-1",
            Trigger = "тест", Origin = "Interactive", Kind = kind,
            Request = backup, Restore = restore,
            ResolvedPassphrase = passphrase, RequiresEncryption = requires,
            Channel = new JobChannel(history, runId),
        };
    }

    // ---- ArchiveCipher.VerifyPassphraseAsync ----

    [Fact]
    public async Task VerifyPassphrase_True_For_Correct_False_For_Wrong()
    {
        var plain = Path.Combine(_root, "p.bin");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(plain, Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
        var enc = Path.Combine(_root, "p.ebk");
        await ArchiveCipher.EncryptAsync(plain, enc, "right-phrase");

        await using (var s = File.OpenRead(enc))
            Assert.True(await ArchiveCipher.VerifyPassphraseAsync(s, "right-phrase"));
        await using (var s = File.OpenRead(enc))
            Assert.False(await ArchiveCipher.VerifyPassphraseAsync(s, "wrong-phrase"));
    }

    [Fact]
    public async Task VerifyPassphrase_True_For_Non_Encrypted_Stream()
    {
        Directory.CreateDirectory(_root);
        var plain = Path.Combine(_root, "plain.zip");
        await File.WriteAllBytesAsync(plain, "PK\x03\x04 not encrypted"u8.ToArray());
        await using var s = File.OpenRead(plain);
        Assert.True(await ArchiveCipher.VerifyPassphraseAsync(s, "anything")); // проверять нечего
    }

    // ---- зашифрованный круг бэкап → restore (FolderStorage; проба пропускается, движок шифрует/расшифровывает) ----

    private StorageStore NewStore(string remote)
    {
        var store = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        store.SaveAllAsync([new SavedStorage { Id = "dest", Name = "D", Kind = StorageKind.LocalFolder, Path = remote }]).GetAwaiter().GetResult();
        return store;
    }

    [Fact]
    public async Task Encrypted_Backup_Then_Restore_With_Correct_Passphrase_RoundTrips()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "doc.txt"), "секрет");
        var remote = Path.Combine(_root, "remote");
        Directory.CreateDirectory(remote);
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var store = NewStore(remote);
        var pw = Encoding.UTF8.GetBytes("correct horse");

        var backup = new BackupRunner(_ => [], Path.Combine(_root, "build"), resolveFolders: ids => ids.ToList(), storages: store);
        var bout = await backup.RunAsync(
            MakeJob(JobKind.Backup, new StartBackupRequest { CustomFolderIds = [src], TargetStorageIds = ["dest"] }, null,
                history, (byte[])pw.Clone(), requires: true),
            CancellationToken.None);
        Assert.True(bout.Success);
        Assert.True(ArchiveCipher.IsEncrypted(Path.Combine(remote, bout.ArchiveName!))); // в хранилище — зашифрован

        var restoreDir = Path.Combine(_root, "restored");
        var restore = new RestoreRunner(store, () => [], Path.Combine(_root, "rbuild"));
        var rout = await restore.RunAsync(
            MakeJob(JobKind.Restore, null,
                new StartRestoreRequest { SourceStorageId = "dest", RemoteName = bout.ArchiveName!, TargetDir = restoreDir, Policy = "Overwrite" },
                history, (byte[])pw.Clone(), requires: true),
            CancellationToken.None);

        Assert.True(rout.Success);
        var restored = Directory.EnumerateFiles(restoreDir, "doc.txt", SearchOption.AllDirectories).Single();
        Assert.Equal("секрет", await File.ReadAllTextAsync(restored));
    }

    [Fact]
    public async Task Encrypted_Restore_With_Wrong_Passphrase_Fails_Cleanly()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "doc.txt"), "секрет");
        var remote = Path.Combine(_root, "remote");
        Directory.CreateDirectory(remote);
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var store = NewStore(remote);

        var backup = new BackupRunner(_ => [], Path.Combine(_root, "build"), resolveFolders: ids => ids.ToList(), storages: store);
        var bout = await backup.RunAsync(
            MakeJob(JobKind.Backup, new StartBackupRequest { CustomFolderIds = [src], TargetStorageIds = ["dest"] }, null,
                history, Encoding.UTF8.GetBytes("right"), requires: true),
            CancellationToken.None);
        Assert.True(bout.Success);

        var restore = new RestoreRunner(store, () => [], Path.Combine(_root, "rbuild"));
        // FolderStorage не seekable → проба пропускается → движок отвергает неверную фразу (InvalidDataException).
        await Assert.ThrowsAsync<InvalidDataException>(() => restore.RunAsync(
            MakeJob(JobKind.Restore, null,
                new StartRestoreRequest { SourceStorageId = "dest", RemoteName = bout.ArchiveName!, TargetDir = Path.Combine(_root, "out"), Policy = "Overwrite" },
                history, Encoding.UTF8.GetBytes("WRONG"), requires: true),
            CancellationToken.None));
    }

    // ---- резолв тикета в StartBackupAsync ----

    [Fact]
    public async Task StartBackup_With_Ticket_Resolves_Passphrase_And_Strips_It_From_Request()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var store = NewStore(Path.Combine(_root, "remote"));
        using var vault = new PassphraseVault();
        var runner = new CapturingRunner();
        await using var jobs = new JobManager(runner, rid => new JobChannel(history, rid));
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), store, "inst", "1.2.0", vault: vault);

        var ticket = vault.Issue(Encoding.UTF8.GetBytes("topsecret"), Caller.OwnerSid, TimeSpan.FromSeconds(30), DateTimeOffset.Now);
        await handlers.StartBackupAsync(
            new StartBackupRequest { ModuleIds = ["x"], Passphrase = PassphraseRef.FromTicket(ticket) }, Caller, default);

        await runner.Done.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(runner.Captured!.RequiresEncryption);
        Assert.Equal(Encoding.UTF8.GetBytes("topsecret"), runner.Captured.ResolvedPassphrase);
        Assert.Null(runner.Captured.Request!.Passphrase); // тикет не оседает в задаче/истории
    }

    [Fact]
    public async Task StartBackup_With_Invalid_Ticket_Is_FailClosed_Fault()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var store = NewStore(Path.Combine(_root, "remote"));
        using var vault = new PassphraseVault();
        await using var jobs = new JobManager(new BackupRunner(_ => []), rid => new JobChannel(history, rid));
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), store, "inst", "1.2.0", vault: vault);

        var ex = await Assert.ThrowsAsync<IpcFaultException>(() => handlers.StartBackupAsync(
            new StartBackupRequest { ModuleIds = ["x"], Passphrase = PassphraseRef.FromTicket("deadbeef") }, Caller, default));
        Assert.Equal(IpcErrorCodes.PassphraseTicketInvalid, ex.Code);
    }
}
