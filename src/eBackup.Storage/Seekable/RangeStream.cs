namespace eBackup.Storage;

/// <summary>
/// Поток поверх «дай байты с offset длиной count» (HTTP Range и т.п.) с окном-буфером:
/// последовательные чтения ZIP не порождают запрос на каждый Read. Синхронный Read
/// блокируется на сетевом запросе — вызывать с фонового потока (ZipArchive читает
/// синхронно).
/// </summary>
public sealed class RangeStream(
    long length,
    Func<long, int, CancellationToken, Task<byte[]>> fetchRange,
    Action? onDispose = null) : Stream
{
    private const int WindowSize = 512 * 1024;

    private byte[] _window = [];
    private long _windowStart;
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position >= length || buffer.Length == 0)
            return 0;

        if (_position < _windowStart || _position >= _windowStart + _window.Length)
        {
            var want = (int)Math.Min(WindowSize, length - _position);
            _window = await fetchRange(_position, want, ct).ConfigureAwait(false);
            _windowStart = _position;
            if (_window.Length == 0)
                return 0;
        }

        var windowOffset = (int)(_position - _windowStart);
        var toCopy = Math.Min(_window.Length - windowOffset, buffer.Length);
        _window.AsSpan(windowOffset, toCopy).CopyTo(buffer.Span);
        _position += toCopy;
        return toCopy;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            onDispose?.Invoke();
        base.Dispose(disposing);
    }
}
