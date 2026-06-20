using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using eBackup.Core.Crypto;
using Xunit;

namespace eBackup.Tests;

public sealed class SeekableEbkeStreamTests : IDisposable
{
    private const int Chunk = 1 << 20; // совпадает с ArchiveCipher.ChunkSize
    private readonly string _root;

    public SeekableEbkeStreamTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-seek-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Random_Access_RoundTrips_Plaintext()
    {
        var plain = Pattern(2 * Chunk + 12345); // 2 полных чанка + хвост
        var enc = await EncryptBytesAsync(plain, "pass");

        await using var fs = File.OpenRead(enc);
        await using var s = await SeekableEbkeStream.OpenAsync(fs, "pass", leaveOpen: true);

        Assert.Equal(plain.Length, s.Length);
        Assert.Equal(plain, ReadAll(s)); // целиком

        // произвольные диапазоны: границы чанков, середина, хвост
        foreach (var (off, len) in new (long off, int len)[]
        {
            (0, 100), (Chunk - 10, 50), (Chunk, 1), (Chunk + 5, 4096),
            (2L * Chunk - 1, 3), (2L * Chunk, 12345), (plain.Length - 7, 7),
        })
        {
            s.Position = off;
            Assert.Equal(plain.Skip((int)off).Take(len).ToArray(), ReadN(s, len));
        }
    }

    [Fact]
    public async Task Empty_Plaintext_Has_Zero_Length()
    {
        var enc = await EncryptBytesAsync([], "pass");
        await using var fs = File.OpenRead(enc);
        await using var s = await SeekableEbkeStream.OpenAsync(fs, "pass", leaveOpen: true);
        Assert.Equal(0, s.Length);
        Assert.Empty(ReadAll(s));
    }

    [Fact]
    public async Task Wrong_Passphrase_Throws_On_Open()
    {
        var enc = await EncryptBytesAsync(Pattern(5000), "right");
        await using var fs = File.OpenRead(enc);
        await Assert.ThrowsAsync<InvalidDataException>(() => SeekableEbkeStream.OpenAsync(fs, "wrong", leaveOpen: true));
    }

    [Fact]
    public async Task Tampered_Chunk_Throws()
    {
        var enc = await EncryptBytesAsync(Pattern(5000), "pass");
        var bytes = await File.ReadAllBytesAsync(enc);
        bytes[61] ^= 0xFF; // первый байт ciphertext: заголовок(41) + len(4) + tag(16) = 61
        await File.WriteAllBytesAsync(enc, bytes);

        await using var fs = File.OpenRead(enc);
        await Assert.ThrowsAsync<InvalidDataException>(() => SeekableEbkeStream.OpenAsync(fs, "pass", leaveOpen: true));
    }

    [Fact]
    public async Task ZipArchive_Browses_And_Extracts_Over_Encrypted_Stream()
    {
        // ZIP с несжимаемой записью > чанка, чтобы пройти границы чанков (имитация реального .ebk)
        var big = new byte[3 * Chunk + 777];
        new Random(1234).NextBytes(big);

        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var e1 = zip.CreateEntry("small.txt", CompressionLevel.NoCompression);
                await using (var w = e1.Open()) await w.WriteAsync(Encoding.UTF8.GetBytes("hello eBackup"));
                var e2 = zip.CreateEntry("big.bin", CompressionLevel.NoCompression);
                await using (var w = e2.Open()) await w.WriteAsync(big);
            }
            zipBytes = ms.ToArray();
        }
        Assert.True(zipBytes.Length > 2 * Chunk); // точно многочанковый

        var enc = await EncryptBytesAsync(zipBytes, "pass");
        await using var fs = File.OpenRead(enc);
        await using var s = await SeekableEbkeStream.OpenAsync(fs, "pass", leaveOpen: true);
        using var read = new ZipArchive(s, ZipArchiveMode.Read);

        // оглавление прочитано без полной расшифровки
        Assert.Equal(["big.bin", "small.txt"], read.Entries.Select(e => e.Name).OrderBy(x => x).ToArray());

        // выборочное извлечение каждой записи
        await using (var es = read.GetEntry("big.bin")!.Open())
        {
            using var outMs = new MemoryStream();
            await es.CopyToAsync(outMs);
            Assert.Equal(big, outMs.ToArray());
        }
        await using (var ss = read.GetEntry("small.txt")!.Open())
        {
            using var sr = new StreamReader(ss);
            Assert.Equal("hello eBackup", await sr.ReadToEndAsync());
        }
    }

    // ---- helpers ----

    private async Task<string> EncryptBytesAsync(byte[] plain, string pass)
    {
        Directory.CreateDirectory(_root);
        var plainPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".bin");
        var encPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ebk");
        await File.WriteAllBytesAsync(plainPath, plain);
        await ArchiveCipher.EncryptAsync(plainPath, encPath, pass);
        return encPath;
    }

    private static byte[] Pattern(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(i % 251);
        return b;
    }

    private static byte[] ReadAll(Stream s)
    {
        s.Position = 0;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] ReadN(Stream s, int n)
    {
        var buf = new byte[n];
        var total = 0;
        while (total < n)
        {
            var r = s.Read(buf, total, n - total);
            if (r == 0) break;
            total += r;
        }
        return total == n ? buf : buf[..total];
    }
}
