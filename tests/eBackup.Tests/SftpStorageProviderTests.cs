using System.Threading.Tasks;
using eBackup.Storage.Sftp;
using Xunit;

namespace eBackup.Tests;

public class SftpStorageProviderTests
{
    [Fact]
    public void Options_Have_Sensible_Defaults()
    {
        var o = new SftpConnectionOptions { Host = "nas.local", Username = "erney" };

        Assert.Equal(22, o.Port);
        Assert.Equal(".", o.RemoteDirectory);
    }

    [Fact]
    public void Name_Includes_User_Host_Port()
    {
        var provider = new SftpStorageProvider(new SftpConnectionOptions
        {
            Host = "nas.local",
            Port = 2222,
            Username = "erney"
        });

        Assert.Equal("SFTP (erney@nas.local:2222)", provider.Name);
    }

    [Fact]
    public async Task TestConnection_Fails_Cleanly_When_No_Credentials()
    {
        // Ни пароля, ни ключа — провайдер не должен бросать исключение,
        // а вернуть понятный отрицательный результат для кнопки «Тест».
        var provider = new SftpStorageProvider(new SftpConnectionOptions
        {
            Host = "nas.local",
            Username = "erney"
        });

        var result = await provider.TestConnectionAsync();

        Assert.False(result.Success);
        Assert.Contains("пароль или приватный ключ", result.Message);
    }
}
