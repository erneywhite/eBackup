using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using eBackup.Core.Crypto;
using Xunit;

namespace eBackup.Tests;

public class ArchiveCipherTests
{
    [Fact]
    public async Task Encrypt_Then_Decrypt_Roundtrips_MultiChunk()
    {
        var plain = Path.Combine(Path.GetTempPath(), $"ebk-p-{Guid.NewGuid():N}.bin");
        var enc = plain + ".enc";
        var dec = plain + ".dec";
        try
        {
            // Больше двух чанков (чанк = 1 MiB) — проверяем потоковую обработку.
            var data = RandomNumberGenerator.GetBytes(2_500_000);
            await File.WriteAllBytesAsync(plain, data);

            Assert.False(ArchiveCipher.IsEncrypted(plain));
            await ArchiveCipher.EncryptAsync(plain, enc, "correct horse battery");
            Assert.True(ArchiveCipher.IsEncrypted(enc));

            await ArchiveCipher.DecryptAsync(enc, dec, "correct horse battery");
            Assert.Equal(data, await File.ReadAllBytesAsync(dec));
        }
        finally
        {
            foreach (var f in new[] { plain, enc, dec })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task Decrypt_With_Wrong_Passphrase_Throws()
    {
        var plain = Path.Combine(Path.GetTempPath(), $"ebk-p-{Guid.NewGuid():N}.bin");
        var enc = plain + ".enc";
        var dec = plain + ".dec";
        try
        {
            await File.WriteAllBytesAsync(plain, RandomNumberGenerator.GetBytes(4096));
            await ArchiveCipher.EncryptAsync(plain, enc, "right-pass");

            await Assert.ThrowsAsync<InvalidDataException>(
                () => ArchiveCipher.DecryptAsync(enc, dec, "wrong-pass"));
        }
        finally
        {
            foreach (var f in new[] { plain, enc, dec })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}
