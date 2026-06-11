using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using eBackup.Core.Security;
using eBackup.Storage;
using eBackup.Storage.Sftp;
using Xunit;

namespace eBackup.Tests;

public class StorageStoreTests
{
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext)
            => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue)
            => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["enc:".Length..]));
    }

    [Fact]
    public async Task Migrates_Legacy_Sftp_Connections_Preserving_Ids_And_Secrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ebk-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var legacyPath = Path.Combine(dir, "connections.json");
        var storagesPath = Path.Combine(dir, "storages.json");
        try
        {
            var protector = new FakeProtector();

            // Старый конфиг, как его писал SftpConnectionStore.
            var legacyStore = new SftpConnectionStore(protector, legacyPath);
            await legacyStore.SaveAllAsync(
            [
                legacyStore.Protect("nas", "Домашний NAS", new SftpConnectionOptions
                {
                    Host = "192.168.1.10",
                    Port = 2022,
                    Username = "backup",
                    Password = "secret-pass",
                    RemoteDirectory = "/backups"
                })
            ]);

            var store = new StorageStore(protector, storagesPath, legacyPath);
            var storages = await store.LoadAsync();

            var nas = storages.Single();
            Assert.Equal("nas", nas.Id);                       // id сохранён — расписания не ломаются
            Assert.Equal(StorageKind.Sftp, nas.Kind);
            Assert.Equal("192.168.1.10", nas.Host);
            Assert.Equal(2022, nas.Port);

            // Секрет переносится как есть и расшифровывается фабрикой.
            var options = StorageFactory.ToSftpOptions(nas, protector);
            Assert.Equal("secret-pass", options.Password);

            Assert.True(File.Exists(storagesPath)); // миграция записала новый конфиг
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task FolderStorage_Upload_List_Download_Delete_Roundtrip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ebk-folder-{Guid.NewGuid():N}");
        var src = Path.Combine(Path.GetTempPath(), $"ebk-src-{Guid.NewGuid():N}.ebk");
        var dst = Path.Combine(Path.GetTempPath(), $"ebk-dst-{Guid.NewGuid():N}.ebk");
        try
        {
            await File.WriteAllTextAsync(src, "archive-bytes");
            var storage = StorageFactory.Create(new SavedStorage
            {
                Id = "net",
                Name = "Сетевой диск",
                Kind = StorageKind.LocalFolder,
                Path = root
            }, new FakeProtector());

            var test = await storage.TestAsync();
            Assert.True(test.Success);

            await storage.UploadAsync(src, "a.ebk");
            var files = await storage.ListDetailedAsync();
            Assert.Equal("a.ebk", files.Single().Name);

            await storage.DownloadAsync("a.ebk", dst);
            Assert.Equal("archive-bytes", await File.ReadAllTextAsync(dst));

            await storage.DeleteAsync("a.ebk");
            Assert.Empty(await storage.ListDetailedAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (File.Exists(src)) File.Delete(src);
            if (File.Exists(dst)) File.Delete(dst);
        }
    }

    [Theory]
    [InlineData(@"\\nas\share\backups\deep", @"\\nas\share")]
    [InlineData(@"\\192.168.1.10\backup", @"\\192.168.1.10\backup")]
    [InlineData(@"\\onlyserver", null)]
    public void Share_Root_Is_Extracted_From_Unc_Path(string path, string? expected)
        => Assert.Equal(expected, FolderStorage.GetShareRoot(path));
}
