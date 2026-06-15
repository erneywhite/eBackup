using System.Buffers.Binary;
using System.Text.Json;
using eBackup.Ipc.Contracts;

namespace eBackup.Ipc.Transport;

/// <summary>
/// Пишет фреймы на стрим: преамбула один раз, далее [u32 LE длина][UTF-8 JSON Frame].
/// Все записи сериализованы одним SemaphoreSlim, чтобы ответы и job-нотификации на одном
/// соединении не «рвали» друг друга. Превышение исходящего лимита — баг, ловим явно.
/// </summary>
public sealed class FrameWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly int _maxFrameBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FrameWriter(Stream stream, int maxFrameBytes = FrameReader.DefaultMaxFrameBytes)
    {
        _stream = stream;
        _maxFrameBytes = maxFrameBytes;
    }

    public async Task WritePreambleAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(IpcContractInfo.Preamble, ct).ConfigureAwait(false);
            await _stream.WriteAsync(new[] { IpcContractInfo.EnvelopeFormat }, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task WriteFrameAsync(Frame frame, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, IpcJsonContext.Default.Frame);
        if (payload.Length > _maxFrameBytes)
            throw new IpcProtocolException(
                $"Исходящий фрейм превышает лимит ({payload.Length} > {_maxFrameBytes}) — крупные ответы надо слать страницами.");

        var lenBuf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)payload.Length);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(lenBuf, ct).ConfigureAwait(false);
            await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
