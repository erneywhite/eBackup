using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.Crypto;
using eBackup.Core.Engine;
using eBackup.Core.History;
using eBackup.Ipc.Contracts;
using eBackup.Security;
using eBackup.Service.Jobs;
using eBackup.Storage;
using Xunit;

namespace eBackup.Tests.Service;

/// <summary>S7h — закалка шифрования: пост-проверка готового архива, анти-двойное-шифрование,
/// гард seek-restore зашифрованного, и инвариант «парольная фраза не попадает в журнал».</summary>
public sealed class S7hHardeningTests : IDisposable
{
    private readonly string _root;

    public S7hHardeningTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-s7h-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    private async Task<string> EncryptTempAsync(byte[] data, string passphrase)
    {
        Directory.CreateDirectory(_root);
        var plain = Path.Combine(_root, $"p-{Guid.NewGuid():N}.bin");
        var enc = Path.Combine(_root, $"e-{Guid.NewGuid():N}.ebk");
        await File.WriteAllBytesAsync(plain, data);
        await ArchiveCipher.EncryptAsync(plain, enc, passphrase);
        File.Delete(plain);
        return enc;
    }

    [Fact]
    public async Task DecryptToStream_RoundTrips_And_Counts_Bytes()
    {
        var data = Enumerable.Range(0, 9000).Select(i => (byte)(i * 7)).ToArray();
        var enc = await EncryptTempAsync(data, "pw");

        using var ms = new MemoryStream();
        var n = await ArchiveCipher.DecryptAsync(enc, ms, "pw");

        Assert.Equal(data.Length, n);
        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public async Task PostVerify_Primitive_Catches_Tampered_Ciphertext()
    {
        var enc = await EncryptTempAsync(Encoding.UTF8.GetBytes("important data"), "pw");
        var bytes = await File.ReadAllBytesAsync(enc);
        bytes[^1] ^= 0xFF;                          // портим хвост (ciphertext/tag последнего чанка)
        await File.WriteAllBytesAsync(enc, bytes);

        // Сквозная проверка (как пост-verify в движке) ловит порчу → InvalidDataException.
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCipher.DecryptAsync(enc, Stream.Null, "pw"));
    }

    [Fact]
    public async Task EncryptAsync_Refuses_Double_Encryption()
    {
        var enc = await EncryptTempAsync(Encoding.UTF8.GetBytes("data"), "pw");
        var enc2 = Path.Combine(_root, "double.ebk");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveCipher.EncryptAsync(enc, enc2, "pw"));
    }

    [Fact]
    public async Task Seek_RestoreStream_Refuses_Encrypted_With_Clear_Message()
    {
        var enc = await EncryptTempAsync(Encoding.UTF8.GetBytes("data"), "pw");
        await using var s = File.OpenRead(enc);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new BackupEngine().RestoreAsync(s));
        Assert.Contains("ифрован", ex.Message); // внятный отказ, а не мутная ZIP-ошибка
    }

    [Fact]
    public async Task Passphrase_Never_Appears_In_History_Journal()
    {
        const string secret = "SUPER-SECRET-PHRASE-must-not-be-logged-42";
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "doc.txt"), "hello");
        var remote = Path.Combine(_root, "remote");
        Directory.CreateDirectory(remote);

        var histRoot = Path.Combine(_root, "hist");
        var history = new HistoryStore(histRoot);
        var store = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));
        await store.SaveAllAsync([new SavedStorage { Id = "dest", Name = "D", Kind = StorageKind.LocalFolder, Path = remote }]);

        var runner = new BackupRunner(_ => [], Path.Combine(_root, "build"), resolveFolders: ids => ids.ToList(), storages: store);
        var runId = "run-" + Guid.NewGuid().ToString("N")[..8];
        var job = new Job
        {
            Seq = 1, JobId = "j1", RunId = runId, OwnerSid = "S-1-5-21-1",
            Trigger = "тест", Origin = "Interactive",
            Request = new StartBackupRequest { CustomFolderIds = [src], TargetStorageIds = ["dest"] },
            ResolvedPassphrase = Encoding.UTF8.GetBytes(secret), RequiresEncryption = true,
            Channel = new JobChannel(history, runId),
        };

        var outcome = await runner.RunAsync(job, CancellationToken.None);
        Assert.True(outcome.Success);
        Assert.DoesNotContain(secret, outcome.Error ?? "");

        // Весь журнал истории на диске (runs.json + per-run логи) НЕ должен содержать фразу.
        foreach (var file in Directory.EnumerateFiles(histRoot, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(secret, text);
        }
    }
}
