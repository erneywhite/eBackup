using System;
using System.IO;
using System.Threading.Tasks;
using eBackup.Security;
using Xunit;

namespace eBackup.Tests;

public sealed class MachineKeyStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _keyFile;

    public MachineKeyStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ebk-ks-{Guid.NewGuid():N}");
        _keyFile = Path.Combine(_dir, "machine.key");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void EnsureKey_Creates_And_Is_Stable()
    {
        var store = new MachineKeyStore(_keyFile);
        var k1 = store.EnsureKey();
        Assert.Equal(32, k1.Length);
        Assert.True(File.Exists(_keyFile));

        Assert.Equal(k1, store.EnsureKey());                       // повторно — тот же ключ
        Assert.Equal(k1, new MachineKeyStore(_keyFile).EnsureKey()); // новый store читает тот же файл
    }

    [Fact]
    public void GetKeyById_Active_Works_Unknown_Throws()
    {
        var store = new MachineKeyStore(_keyFile);
        var key = store.EnsureKey();
        Assert.Equal(key, store.GetKeyById(store.ActiveKeyId));
        Assert.Throws<MachineKeyUnavailableException>(() => store.GetKeyById(Guid.NewGuid()));
    }

    [Fact]
    public void Corrupted_File_Throws_Corrupted_Not_Regenerate()
    {
        new MachineKeyStore(_keyFile).EnsureKey();
        var bytes = File.ReadAllBytes(_keyFile);
        bytes[^1] ^= 0xFF; // ломаем SHA-256 хвост
        File.WriteAllBytes(_keyFile, bytes);

        Assert.Throws<MachineKeyCorruptedException>(() => new MachineKeyStore(_keyFile).EnsureKey());
    }

    [Fact]
    public async Task Concurrent_EnsureKey_Yields_One_Key()
    {
        var s1 = new MachineKeyStore(_keyFile);
        var s2 = new MachineKeyStore(_keyFile);
        var keys = await Task.WhenAll(Task.Run(() => s1.EnsureKey()), Task.Run(() => s2.EnsureKey()));
        Assert.Equal(keys[0], keys[1]); // оба получили ключ победителя гонки
    }
}
