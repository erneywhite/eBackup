using System;
using System.IO;
using System.Threading.Tasks;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using eBackup.Security;
using eBackup.Service;
using eBackup.Storage;
using Xunit;

namespace eBackup.Tests.Service;

public sealed class ArchiveReaderTests : IDisposable
{
    private readonly string _root;

    public ArchiveReaderTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-archread-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly CallerContext Caller = new("S-1-5-21-1", IsAdmin: false);

    private StorageStore NewStore() => new(
        new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
        Path.Combine(_root, "cfg", "storages.json"), Path.Combine(_root, "cfg", "connections.json"));

    [Fact]
    public async Task Open_Read_Close_LocalFolder_Archive()
    {
        var dir = Path.Combine(_root, "store");
        Directory.CreateDirectory(dir);
        var bytes = new byte[3000];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        await File.WriteAllBytesAsync(Path.Combine(dir, "a.ebk"), bytes);

        var store = NewStore();
        await store.SaveAllAsync([new SavedStorage { Id = "d", Name = "D", Kind = StorageKind.LocalFolder, Path = dir }]);

        var reader = new ServiceArchiveReader(store);
        var open = await reader.OpenAsync(new OpenArchiveReadRequest { StorageId = "d", RemoteName = "a.ebk" }, Caller, default);
        Assert.Equal(3000, open.Length);
        Assert.NotEqual("", open.Handle);

        var chunk = await reader.ReadAsync(open.Handle, 100, 500, default);
        Assert.Equal(bytes[100..600], chunk);

        var tail = await reader.ReadAsync(open.Handle, 2900, 500, default); // короткий хвост у конца файла
        Assert.Equal(bytes[2900..3000], tail);

        reader.Close(open.Handle);
        await Assert.ThrowsAsync<IpcFaultException>(() => reader.ReadAsync(open.Handle, 0, 10, default)); // сессии больше нет
    }

    [Fact]
    public async Task Open_Missing_Storage_Throws_NotFound()
    {
        var reader = new ServiceArchiveReader(NewStore());
        await Assert.ThrowsAsync<IpcFaultException>(
            () => reader.OpenAsync(new OpenArchiveReadRequest { StorageId = "nope", RemoteName = "x.ebk" }, Caller, default));
    }
}
