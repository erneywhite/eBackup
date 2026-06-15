using System.Buffers.Binary;
using System.Text.Json;
using eBackup.Ipc.Contracts;

namespace eBackup.Ipc.Transport;

/// <summary>
/// Читает фреймы со стрима: [преамбула EBKI+формат] один раз, далее [u32 LE длина][UTF-8 JSON Frame].
/// Размер проверяется ДО аллокации (DoS), на «застрявший» фрейм — таймаут, обрыв посреди — ошибка.
/// Тело Frame остаётся сырым JsonElement (двухстадийный декод — раскручивается выше по op).
/// </summary>
public sealed class FrameReader
{
    /// <summary>Жёсткий потолок размера входящего фрейма (по умолчанию 1 МиБ; сервер для запросов задаёт меньше).</summary>
    public const int DefaultMaxFrameBytes = 1 << 20;

    private readonly Stream _stream;
    private readonly int _maxFrameBytes;
    private readonly TimeSpan _frameTimeout;

    public FrameReader(Stream stream, int maxFrameBytes = DefaultMaxFrameBytes, TimeSpan? frameTimeout = null)
    {
        _stream = stream;
        _maxFrameBytes = maxFrameBytes;
        _frameTimeout = frameTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Прочитать и проверить преамбулу соединения. Несовпадение → ошибка (выше = VersionMismatch).</summary>
    public async Task ReadPreambleAsync(CancellationToken ct = default)
    {
        var buf = new byte[IpcContractInfo.Preamble.Length + 1];
        try
        {
            await _stream.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new IpcProtocolException("Соединение закрылось до преамбулы.");
        }
        if (!buf.AsSpan(0, IpcContractInfo.Preamble.Length).SequenceEqual(IpcContractInfo.Preamble))
            throw new IpcProtocolException("Неверная преамбула — это не eBackup IPC.");
        if (buf[^1] != IpcContractInfo.EnvelopeFormat)
            throw new IpcProtocolException($"Несовместимый формат конверта: {buf[^1]}.");
    }

    /// <summary>Прочитать следующий фрейм. null — чистое закрытие на границе фреймов.</summary>
    public async Task<Frame?> ReadFrameAsync(CancellationToken ct = default)
    {
        var lenBuf = new byte[4];
        var got = 0;
        while (got < 4)
        {
            var r = await _stream.ReadAsync(lenBuf.AsMemory(got, 4 - got), ct).ConfigureAwait(false);
            if (r == 0) break;
            got += r;
        }
        if (got == 0) return null;                  // чистое закрытие между фреймами
        if (got < 4) throw new IpcProtocolException("Обрыв в префиксе длины фрейма.");

        var len = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
        if (len == 0) throw new IpcProtocolException("Нулевая длина фрейма.");
        if (len > (uint)_maxFrameBytes)
            throw new IpcProtocolException($"Фрейм превышает лимит ({len} > {_maxFrameBytes}).");

        var payload = new byte[len]; // аллокация только после проверки размера
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_frameTimeout); // начатый фрейм обязан прийти целиком вовремя
        try
        {
            await _stream.ReadExactlyAsync(payload, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new IpcProtocolException("Обрыв посреди фрейма.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new IpcProtocolException("Таймаут чтения фрейма.");
        }

        return JsonSerializer.Deserialize(payload, IpcJsonContext.Default.Frame)
            ?? throw new IpcProtocolException("Пустой/нечитаемый фрейм.");
    }
}
