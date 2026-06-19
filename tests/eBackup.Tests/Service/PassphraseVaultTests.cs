using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
using TR = eBackup.Service.Jobs.PassphraseVault.TicketResult;

namespace eBackup.Tests.Service;

public sealed class PassphraseVaultTests : IDisposable
{
    private readonly string _root;
    private static readonly DateTimeOffset T0 = new(2026, 6, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public PassphraseVaultTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-vault-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Issue_Then_Consume_Once_Returns_Phrase_Then_Single_Use_Gone()
    {
        using var v = new PassphraseVault();
        var t = v.Issue(Bytes("secret"), "sid", Ttl, T0);

        var (r1, b1) = v.Consume(t, "sid", T0.AddSeconds(1));
        Assert.Equal(TR.Ok, r1);
        Assert.Equal(Bytes("secret"), b1);

        var (r2, b2) = v.Consume(t, "sid", T0.AddSeconds(2)); // повторно — нельзя
        Assert.Equal(TR.NotFound, r2);
        Assert.Null(b2);
    }

    [Fact]
    public void Wrong_Owner_Is_Rejected_And_Burns_The_Ticket()
    {
        using var v = new PassphraseVault();
        var t = v.Issue(Bytes("secret"), "sid-A", Ttl, T0);

        var (r, b) = v.Consume(t, "sid-B", T0.AddSeconds(1));
        Assert.Equal(TR.WrongOwner, r);
        Assert.Null(b);

        // single-use: даже верный владелец больше не получит (тикет сгорел)
        Assert.Equal(TR.NotFound, v.Consume(t, "sid-A", T0.AddSeconds(2)).Result);
    }

    [Fact]
    public void Expired_Ticket_Is_Rejected()
    {
        using var v = new PassphraseVault();
        var t = v.Issue(Bytes("secret"), "sid", Ttl, T0);
        var (r, b) = v.Consume(t, "sid", T0.AddSeconds(31)); // за пределами TTL
        Assert.Equal(TR.Expired, r);
        Assert.Null(b);
    }

    [Fact]
    public async Task Concurrent_Consume_Has_Exactly_One_Winner()
    {
        using var v = new PassphraseVault();
        var t = v.Issue(Bytes("secret"), "sid", Ttl, T0);

        var oks = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            var (r, b) = v.Consume(t, "sid", T0.AddSeconds(1));
            if (r == TR.Ok && b is not null) Interlocked.Increment(ref oks);
        })));

        Assert.Equal(1, oks); // анти-replay/TOCTOU: ровно один поток забрал фразу
    }

    [Fact]
    public void Caller_May_Zero_Its_Own_Buffer_After_Issue()
    {
        using var v = new PassphraseVault();
        var buf = Bytes("phrase");
        var t = v.Issue(buf, "sid", Ttl, T0);
        CryptographicOperations.ZeroMemory(buf); // вызывающий затирает свою копию

        var (r, b) = v.Consume(t, "sid", T0.AddSeconds(1));
        Assert.Equal(TR.Ok, r);
        Assert.Equal(Bytes("phrase"), b); // хранимая копия не пострадала
    }

    [Fact]
    public void Issue_After_Dispose_Throws()
    {
        var v = new PassphraseVault();
        v.Dispose();
        Assert.Throws<ObjectDisposedException>(() => v.Issue(Bytes("x"), "sid", Ttl, T0));
    }

    // ---- StashPassphrase handler ----

    private static readonly CallerContext Caller = new("S-1-5-21-1", IsAdmin: false);

    private (ServiceHandlers handlers, JobManager jobs) BuildHandlers(PassphraseVault vault)
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var storages = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        var jobs = new JobManager(new BackupRunner(_ => []), rid => new JobChannel(history, rid));
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), storages, "inst", "1.2.0", vault: vault);
        return (handlers, jobs);
    }

    [Fact]
    public async Task StashPassphrase_Issues_A_Consumable_Ticket()
    {
        using var vault = new PassphraseVault();
        var (handlers, jobs) = BuildHandlers(vault);
        await using var _ = jobs;

        var resp = await handlers.StashPassphraseAsync(new StashPassphraseRequest { Plaintext = "hunter2" }, Caller, default);
        Assert.NotEmpty(resp.Ticket);
        Assert.True(resp.ExpiresAt > DateTimeOffset.Now);

        var (r, b) = vault.Consume(resp.Ticket, Caller.OwnerSid, DateTimeOffset.Now);
        Assert.Equal(TR.Ok, r);
        Assert.Equal("hunter2", Encoding.UTF8.GetString(b!));
    }

    [Fact]
    public async Task StashPassphrase_Empty_Is_BadRequest()
    {
        using var vault = new PassphraseVault();
        var (handlers, jobs) = BuildHandlers(vault);
        await using var _ = jobs;
        await Assert.ThrowsAsync<IpcFaultException>(
            () => handlers.StashPassphraseAsync(new StashPassphraseRequest { Plaintext = "" }, Caller, default));
    }
}
