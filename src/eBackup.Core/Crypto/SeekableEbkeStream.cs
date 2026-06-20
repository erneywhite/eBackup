using System.Buffers.Binary;
using System.Security.Cryptography;

namespace eBackup.Core.Crypto;

/// <summary>
/// Seekable поток ОТКРЫТОГО текста поверх зашифрованного EBKE-потока. Читает заголовок, выводит ключ
/// (Argon2id — один раз при открытии) и по запросу Read/Seek подтягивает + дешифрует ТОЛЬКО нужные
/// чанки (с небольшим LRU-кэшем). Поверх него работает обычный <c>ZipArchive</c> — оглавление и
/// выборочное извлечение без полной расшифровки/закачки (важно для зашифрованных архивов 100+ ГБ).
///
/// Формат EBKE (см. <see cref="ArchiveCipher"/>): заголовок 41 байт, далее чанки
/// <c>[len:i32][tag:16][cipher:len]</c> по <c>chunk</c> байт открытого текста; nonce каждого чанка —
/// <c>noncePrefix(4) || индекс(8, big-endian)</c>. Это даёт прямое отображение «смещение → чанк».
///
/// НЕ потокобезопасен (одна операция за раз), как и нижележащий seek-поток хранилища. При выборочном
/// чтении проверяются GCM-теги только ПРОЧИТАННЫХ чанков (в этом и смысл — не читать весь архив);
/// привязка nonce к индексу ловит перестановку чанков. Полную верификацию даёт <see cref="ArchiveCipher.DecryptAsync(string, Stream, string, CancellationToken)"/>.
/// </summary>
public sealed class SeekableEbkeStream : Stream
{
    // Раскладка заголовка: Magic(4) ver(1) memKb(4) iters(4) par(4) chunk(4) salt(16) noncePrefix(4).
    private const int HeaderSize = 41;
    private const int ChunkOverhead = 4 + 16; // len + GCM-тег перед ciphertext каждого чанка
    private const int CacheCapacity = 8;       // последних дешифрованных чанков (по chunkSize каждый)

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly byte[] _key;
    private readonly byte[] _noncePrefix;
    private readonly AesGcm _aes;
    private readonly int _chunkSize;
    private readonly long _stride;      // размер полного чанка НА ДИСКЕ = ChunkOverhead + chunkSize
    private readonly long _length;      // длина открытого текста

    private readonly Dictionary<long, byte[]> _cache = new();
    private readonly LinkedList<long> _lru = new();
    private long _position;
    private bool _disposed;

    private SeekableEbkeStream(Stream inner, bool leaveOpen, byte[] key, byte[] noncePrefix, int chunkSize)
    {
        _inner = inner;
        _leaveOpen = leaveOpen;
        _key = key;
        _noncePrefix = noncePrefix;
        _chunkSize = chunkSize;
        _aes = new AesGcm(key, 16);
        _stride = ChunkOverhead + (long)chunkSize;
        _length = ComputePlainLength(inner.Length);
    }

    /// <summary>
    /// Открыть зашифрованный seek-поток как поток открытого текста. Выводит ключ (Argon2id) и сразу
    /// проверяет фразу по первому чанку: неверная фраза/повреждение → <see cref="InvalidDataException"/>.
    /// </summary>
    public static async Task<SeekableEbkeStream> OpenAsync(
        Stream encrypted, string passphrase, bool leaveOpen = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        if (!encrypted.CanSeek)
            throw new ArgumentException("Нужен поток с произвольным доступом (CanSeek).", nameof(encrypted));

        encrypted.Position = 0;
        var header = new byte[HeaderSize];
        await ReadExactAsync(encrypted, header, ct).ConfigureAwait(false);

        if (!header.AsSpan(0, 4).SequenceEqual(ArchiveCipher.Magic))
            throw new InvalidDataException("Файл не является зашифрованным архивом eBackup.");
        if (header[4] != ArchiveCipher.Version)
            throw new InvalidDataException("Неподдерживаемая версия формата шифрования.");

        var memKb = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(5));
        var iters = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(9));
        var parallelism = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(13));
        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(17));
        if (chunkSize is <= 0 or > (64 << 20))
            throw new InvalidDataException("Повреждён заголовок зашифрованного архива.");

        var salt = header[21..37];
        var noncePrefix = header[37..41];

        var key = await Task.Run(() => ArchiveCipher.DeriveKey(passphrase, salt, memKb, iters, parallelism), ct)
            .ConfigureAwait(false);

        var stream = new SeekableEbkeStream(encrypted, leaveOpen, key, noncePrefix, chunkSize);
        try
        {
            if (stream._length > 0)
                _ = stream.GetChunk(0); // проверяем фразу по первому чанку (бросит InvalidDataException)
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        return stream;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var total = 0;
        while (!buffer.IsEmpty && _position < _length)
        {
            var index = _position / _chunkSize;
            var within = (int)(_position % _chunkSize);
            var plain = GetChunk(index);
            if (within >= plain.Length)
                break; // защита от рассинхрона длины (на практике не случается)

            var available = (int)Math.Min(plain.Length - within, _length - _position);
            var n = Math.Min(available, buffer.Length);
            plain.AsSpan(within, n).CopyTo(buffer);
            buffer = buffer[n..];
            _position += n;
            total += n;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
            throw new IOException("Отрицательная позиция в потоке.");
        _position = target; // за концом читать можно — Read вернёт 0 (как у FileStream)
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Длина открытого текста по размеру зашифрованного файла (все чанки кроме последнего — полные).</summary>
    private long ComputePlainLength(long encryptedLength)
    {
        var body = encryptedLength - HeaderSize;
        if (body <= 0)
            return 0;

        var rem = body % _stride;
        if (rem == 0)
            return body / _stride * _chunkSize;          // последний чанк тоже полный

        if (rem <= ChunkOverhead)
            throw new InvalidDataException("Повреждён зашифрованный архив (хвостовой чанк).");
        var fullChunks = body / _stride;
        var lastPlain = rem - ChunkOverhead;
        return fullChunks * _chunkSize + lastPlain;
    }

    /// <summary>Дешифрованный открытый текст чанка <paramref name="index"/> (из кэша или с подтягиванием).</summary>
    private byte[] GetChunk(long index)
    {
        if (_cache.TryGetValue(index, out var cached))
        {
            _lru.Remove(index);
            _lru.AddLast(index);
            return cached;
        }

        var plain = FetchAndDecrypt(index);

        _cache[index] = plain;
        _lru.AddLast(index);
        if (_cache.Count > CacheCapacity)
        {
            var evict = _lru.First!.Value;
            _lru.RemoveFirst();
            _cache.Remove(evict);
        }
        return plain;
    }

    private byte[] FetchAndDecrypt(long index)
    {
        _inner.Position = HeaderSize + index * _stride;

        Span<byte> lenBuf = stackalloc byte[4];
        ReadExact(_inner, lenBuf);
        var len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (len <= 0 || len > _chunkSize)
            throw new InvalidDataException("Повреждён зашифрованный архив (размер чанка).");

        var tag = new byte[16];
        ReadExact(_inner, tag);
        var cipher = new byte[len];
        ReadExact(_inner, cipher);

        Span<byte> nonce = stackalloc byte[12];
        _noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], (ulong)index);

        var plain = new byte[len];
        try
        {
            _aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("Неверная парольная фраза или повреждённый архив.");
        }
        return plain;
    }

    private static void ReadExact(Stream s, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer[total..]);
            if (n == 0)
                throw new InvalidDataException("Неожиданный конец зашифрованного архива.");
            total += n;
        }
    }

    private static async Task ReadExactAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0)
                throw new InvalidDataException("Неожиданный конец зашифрованного архива.");
            total += n;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_key);
            _cache.Clear();
            _lru.Clear();
            if (!_leaveOpen)
                _inner.Dispose();
        }
        _disposed = true;
        base.Dispose(disposing);
    }
}
