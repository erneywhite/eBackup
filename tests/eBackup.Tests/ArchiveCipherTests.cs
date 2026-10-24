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

    [Theory]
    [InlineData(5, int.MaxValue)]          // memKb ≈ 2 ТБ
    [InlineData(9, int.MaxValue)]          // iterations
    [InlineData(13, 100_000)]              // parallelism
    [InlineData(13, 0)]                    // parallelism = 0
    public async Task Decrypt_Rejects_Absurd_Kdf_Params_From_Header(int offset, int value)
    {
        // Заголовок не аутентифицирован: подменённые параметры Argon2 не должны съесть память/CPU —
        // отказ ДО вывода ключа. Раскладка: Magic(4) ver(1) memKb(4) iters(4) par(4) ...
        var plain = Path.Combine(Path.GetTempPath(), $"ebk-p-{Guid.NewGuid():N}.bin");
        var enc = plain + ".enc";
        try
        {
            await File.WriteAllBytesAsync(plain, RandomNumberGenerator.GetBytes(1024));
            await ArchiveCipher.EncryptAsync(plain, enc, "pass");
            var bytes = await File.ReadAllBytesAsync(enc);
            BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), value);
            await File.WriteAllBytesAsync(enc, bytes);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => ArchiveCipher.DecryptAsync(enc, Stream.Null, "pass"));
            await using var fs = File.OpenRead(enc);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => ArchiveCipher.VerifyPassphraseAsync(fs, "pass"));
        }
        finally
        {
            foreach (var f in new[] { plain, enc })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}
