using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace eBackup.Core.Crypto;

/// <summary>
/// Шифрование архива .ebk: AES-256-GCM по чанкам, ключ из парольной фразы через Argon2id.
///
/// Формат файла:
///   [Magic "EBKE"][version:1][memKb:i32][iters:i32][parallelism:i32][chunk:i32]
///   [salt:16][noncePrefix:4]
///   далее чанки: [len:i32][tag:16][ciphertext:len] ...
/// nonce = noncePrefix(4) || counter(8, big-endian) — уникален для каждого чанка.
/// Заголовок не зашифрован (соль/параметры KDF), но GCM-тег защищает каждый чанк от подмены.
/// </summary>
public static class ArchiveCipher
{
    private static readonly byte[] Magic = "EBKE"u8.ToArray();
    private const byte Version = 1;
    private const int ChunkSize = 1 << 20;          // 1 MiB
    private const int Argon2MemoryKb = 64 * 1024;   // 64 MiB
    private const int Argon2Iterations = 3;
    private const int Argon2Parallelism = 4;

    /// <summary>Начинается ли файл с нашей сигнатуры (т.е. зашифрован).</summary>
    public static bool IsEncrypted(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return IsEncrypted(fs);
    }

    /// <summary>То же по открытому seek-потоку; позиция восстанавливается.</summary>
    public static bool IsEncrypted(Stream stream)
    {
        var position = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> head = stackalloc byte[4];
            var n = stream.Read(head);
            return n == 4 && head.SequenceEqual(Magic);
        }
        finally
        {
            stream.Position = position;
        }
    }

    /// <summary>
    /// Лёгкая проверка фразы по НАЧАЛУ потока: читает заголовок + ПЕРВЫЙ чанк и проверяет GCM-тег.
    /// true — фраза верна (или поток не наш формат / пуст — проверять нечего); false — фраза неверна.
    /// Бросает <see cref="InvalidDataException"/> только на повреждённом/неподдерживаемом заголовке.
    /// Читает лишь первый чанк (≤ chunkSize) — для seek-потока хранилища НЕ качает весь архив.
    /// </summary>
    public static async Task<bool> VerifyPassphraseAsync(Stream stream, string passphrase, CancellationToken ct = default)
    {
        var magic = new byte[4];
        if (await ReadBlockAsync(stream, magic, ct).ConfigureAwait(false) != 4 || !magic.AsSpan().SequenceEqual(Magic))
            return true; // не наш зашифрованный формат — фразу проверять не на чем

        if (stream.ReadByte() != Version)
            throw new InvalidDataException("Неподдерживаемая версия формата шифрования.");

        var memKb = await ReadInt32Async(stream, ct).ConfigureAwait(false);
        var iterations = await ReadInt32Async(stream, ct).ConfigureAwait(false);
        var parallelism = await ReadInt32Async(stream, ct).ConfigureAwait(false);
        var chunkSize = await ReadInt32Async(stream, ct).ConfigureAwait(false);
        if (chunkSize is <= 0 or > (64 << 20))
            throw new InvalidDataException("Повреждён заголовок зашифрованного архива.");

        var salt = new byte[16];
        await ReadExactAsync(stream, salt, ct).ConfigureAwait(false);
        var noncePrefix = new byte[4];
        await ReadExactAsync(stream, noncePrefix, ct).ConfigureAwait(false);

        var key = DeriveKey(passphrase, salt, memKb, iterations, parallelism);
        try
        {
            var lenBuf = new byte[4];
            if (await ReadBlockAsync(stream, lenBuf, ct).ConfigureAwait(false) == 0)
                return true; // пустой архив — чанков нет, проверять нечего

            var len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (len <= 0 || len > chunkSize)
                throw new InvalidDataException("Повреждён зашифрованный архив (размер чанка).");

            var tag = new byte[16];
            var cipher = new byte[len];
            var plain = new byte[len];
            await ReadExactAsync(stream, tag, ct).ConfigureAwait(false);
            await ReadExactAsync(stream, cipher, ct).ConfigureAwait(false);

            using var aes = new AesGcm(key, 16);
            var nonce = new byte[12];
            noncePrefix.CopyTo(nonce, 0);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), 0UL); // первый чанк — counter 0
            try
            {
                aes.Decrypt(nonce, cipher, tag, plain);
                return true;
            }
            catch (AuthenticationTagMismatchException)
            {
                return false; // тег не сошёлся → неверная фраза
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static async Task EncryptAsync(string plainPath, string encryptedPath, string passphrase, CancellationToken ct = default)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var noncePrefix = RandomNumberGenerator.GetBytes(4);
        var key = DeriveKey(passphrase, salt, Argon2MemoryKb, Argon2Iterations, Argon2Parallelism);

        await using var input = File.OpenRead(plainPath);
        await using var output = File.Create(encryptedPath);

        await output.WriteAsync(Magic, ct).ConfigureAwait(false);
        output.WriteByte(Version);
        await WriteInt32Async(output, Argon2MemoryKb, ct).ConfigureAwait(false);
        await WriteInt32Async(output, Argon2Iterations, ct).ConfigureAwait(false);
        await WriteInt32Async(output, Argon2Parallelism, ct).ConfigureAwait(false);
        await WriteInt32Async(output, ChunkSize, ct).ConfigureAwait(false);
        await output.WriteAsync(salt, ct).ConfigureAwait(false);
        await output.WriteAsync(noncePrefix, ct).ConfigureAwait(false);

        using var aes = new AesGcm(key, 16);
        var plain = new byte[ChunkSize];
        var cipher = new byte[ChunkSize];
        var tag = new byte[16];
        var nonce = new byte[12];
        noncePrefix.CopyTo(nonce, 0);
        ulong counter = 0;

        int read;
        while ((read = await ReadBlockAsync(input, plain, ct).ConfigureAwait(false)) > 0)
        {
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter++);
            aes.Encrypt(nonce, plain.AsSpan(0, read), cipher.AsSpan(0, read), tag);

            await WriteInt32Async(output, read, ct).ConfigureAwait(false);
            await output.WriteAsync(tag, ct).ConfigureAwait(false);
            await output.WriteAsync(cipher.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    public static async Task DecryptAsync(string encryptedPath, string plainPath, string passphrase, CancellationToken ct = default)
    {
        await using var input = File.OpenRead(encryptedPath);

        var magic = new byte[4];
        await ReadExactAsync(input, magic, ct).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Файл не является зашифрованным архивом eBackup.");

        if (input.ReadByte() != Version)
            throw new InvalidDataException("Неподдерживаемая версия формата шифрования.");

        var memKb = await ReadInt32Async(input, ct).ConfigureAwait(false);
        var iterations = await ReadInt32Async(input, ct).ConfigureAwait(false);
        var parallelism = await ReadInt32Async(input, ct).ConfigureAwait(false);
        var chunkSize = await ReadInt32Async(input, ct).ConfigureAwait(false);
        if (chunkSize is <= 0 or > (64 << 20))
            throw new InvalidDataException("Повреждён заголовок зашифрованного архива.");

        var salt = new byte[16];
        await ReadExactAsync(input, salt, ct).ConfigureAwait(false);
        var noncePrefix = new byte[4];
        await ReadExactAsync(input, noncePrefix, ct).ConfigureAwait(false);

        var key = DeriveKey(passphrase, salt, memKb, iterations, parallelism);

        await using var output = File.Create(plainPath);
        using var aes = new AesGcm(key, 16);
        var nonce = new byte[12];
        noncePrefix.CopyTo(nonce, 0);
        var tag = new byte[16];
        var cipher = new byte[chunkSize];
        var plain = new byte[chunkSize];
        var lenBuf = new byte[4];
        ulong counter = 0;

        while (true)
        {
            var got = await ReadBlockAsync(input, lenBuf, ct).ConfigureAwait(false);
            if (got == 0) break;                 // корректный конец
            if (got < 4) throw new InvalidDataException("Повреждён зашифрованный архив (заголовок чанка).");

            var len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (len <= 0 || len > chunkSize)
                throw new InvalidDataException("Повреждён зашифрованный архив (размер чанка).");

            await ReadExactAsync(input, tag, ct).ConfigureAwait(false);
            await ReadExactAsync(input, cipher.AsMemory(0, len), ct).ConfigureAwait(false);

            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter++);
            try
            {
                aes.Decrypt(nonce, cipher.AsSpan(0, len), tag, plain.AsSpan(0, len));
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new InvalidDataException("Неверная парольная фраза или повреждённый архив.");
            }

            await output.WriteAsync(plain.AsMemory(0, len), ct).ConfigureAwait(false);
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int memKb, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(passphrase))
        {
            Salt = salt,
            MemorySize = memKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };
        return argon2.GetBytes(32); // 256-битный ключ для AES-256
    }

    private static async Task<int> ReadBlockAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static async Task ReadExactAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        if (await ReadBlockAsync(s, buffer, ct).ConfigureAwait(false) != buffer.Length)
            throw new InvalidDataException("Неожиданный конец зашифрованного архива.");
    }

    private static async Task WriteInt32Async(Stream s, int value, CancellationToken ct)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        await s.WriteAsync(buf, ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadInt32Async(Stream s, CancellationToken ct)
    {
        var buf = new byte[4];
        await ReadExactAsync(s, buf, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buf);
    }
}
