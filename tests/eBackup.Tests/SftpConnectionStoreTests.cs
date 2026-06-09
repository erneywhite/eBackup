using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using eBackup.Core.Security;
using eBackup.Storage.Sftp;
using Xunit;

namespace eBackup.Tests;

public class SftpConnectionStoreTests
{
    /// <summary>Обратимый «шифровальщик» для тестов — без DPAPI, но не plaintext.</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext)
            => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue)
            => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["enc:".Length..]));
    }

    [Fact]
    public async Task Save_Then_Load_Roundtrips_And_Does_Not_Store_Plaintext_Secret()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ebackup-conn-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SftpConnectionStore(new FakeProtector(), tmp);
            var options = new SftpConnectionOptions
            {
                Host = "nas.local",
                Username = "erney",
                Password = "super-secret",
                RemoteDirectory = "/backups"
            };

            await store.SaveAllAsync([store.Protect("nas", "Домашний NAS", options)]);

            // 1) Пароль не должен присутствовать в файле в открытом виде.
            var raw = await File.ReadAllTextAsync(tmp);
            Assert.DoesNotContain("super-secret", raw);

            // 2) Загрузка + расшифровка возвращают исходные параметры.
            var loaded = await store.LoadAsync();
            var back = store.Unprotect(loaded.Single());

            Assert.Equal("nas.local", back.Host);
            Assert.Equal("erney", back.Username);
            Assert.Equal("super-secret", back.Password);
            Assert.Equal("/backups", back.RemoteDirectory);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    [Fact]
    public async Task Load_Returns_Empty_When_File_Missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"ebackup-missing-{Guid.NewGuid():N}.json");
        var store = new SftpConnectionStore(new FakeProtector(), missing);

        var loaded = await store.LoadAsync();

        Assert.Empty(loaded);
    }
}
