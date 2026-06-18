using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Transport;

namespace eBackup.Ipc.Client;

/// <summary>
/// Клиент IPC поверх уже подключённого duplex-стрима (named pipe). Один фоновый цикл чтения
/// раздаёт ответы/ошибки ожидающим запросам по correlation-id; note-фреймы (прогресс) пока
/// игнорируются — стриминг AttachToJob придёт на S4d. Подключение к пайпу и проверку
/// owner==SYSTEM делает отдельный хелпер (production) — здесь чистая транспортная логика,
/// проверяемая по живому пайпу в одном процессе.
/// </summary>
public sealed class IpcClient : IDisposable
{
    private static readonly IpcJsonContext Ctx = IpcJsonContext.Default;

    private readonly FrameReader _reader;
    private readonly FrameWriter _writer;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Frame>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _idCounter;
    private Task? _readLoop;
    private bool _disposed;

    private IpcClient(Stream stream)
    {
        _reader = new FrameReader(stream);
        _writer = new FrameWriter(stream);
    }

    /// <summary>Обменяться преамбулами и запустить фоновый цикл чтения.</summary>
    public static async Task<IpcClient> StartAsync(Stream stream, CancellationToken ct = default)
    {
        var client = new IpcClient(stream);
        await client._writer.WritePreambleAsync(ct).ConfigureAwait(false);
        await client._reader.ReadPreambleAsync(ct).ConfigureAwait(false);
        client._readLoop = Task.Run(() => client.ReadLoopAsync(client._cts.Token));
        return client;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await _reader.ReadFrameAsync(ct).ConfigureAwait(false);
                if (frame is null) break; // соединение закрыто

                if (frame.Kind is FrameKinds.Resp or FrameKinds.Fault
                    && frame.Id is { } id && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(frame);
                }
                // note-фреймы: игнорируем до S4d (стриминг прогресса)
            }
        }
        catch
        {
            // обрыв соединения — ниже завершим все ожидающие запросы
        }
        finally
        {
            foreach (var kv in _pending)
                kv.Value.TrySetException(new IpcConnectionClosedException());
            _pending.Clear();
        }
    }

    public async Task<TResp> RequestAsync<TReq, TResp>(
        string op, TReq req, JsonTypeInfo<TReq> reqTi, JsonTypeInfo<TResp> respTi, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _idCounter).ToString();
        var tcs = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var frame = new Frame
        {
            Kind = FrameKinds.Req,
            Id = id,
            Op = op,
            Body = JsonSerializer.SerializeToElement(req, reqTi),
        };

        await using var reg = ct.Register(() => { if (_pending.TryRemove(id, out var t)) t.TrySetCanceled(ct); });
        try
        {
            await _writer.WriteFrameAsync(frame, ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        var resp = await tcs.Task.ConfigureAwait(false);
        if (resp.Kind == FrameKinds.Fault)
            throw new IpcRequestException(resp.Body?.Deserialize(Ctx.IpcError) ?? new IpcError());

        return resp.Body!.Value.Deserialize(respTi)!;
    }

    // --- удобные обёртки (полный набор операций добьём при wiring GUI на S4e) ---

    public Task<HelloResponse> HelloAsync(HelloRequest req, CancellationToken ct = default)
        => RequestAsync(IpcOps.Hello, req, Ctx.HelloRequest, Ctx.HelloResponse, ct);

    public Task<StartBackupResponse> StartBackupAsync(StartBackupRequest req, CancellationToken ct = default)
        => RequestAsync(IpcOps.StartBackup, req, Ctx.StartBackupRequest, Ctx.StartBackupResponse, ct);

    public Task<JobStatus> GetJobAsync(GetJobRequest req, CancellationToken ct = default)
        => RequestAsync(IpcOps.GetJob, req, Ctx.GetJobRequest, Ctx.JobStatus, ct);

    public void Dispose()
    {
        if (_disposed) return; // идемпотентно — безопасно звать повторно (явно + через using)
        _disposed = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _writer.Dispose();
        _cts.Dispose();
    }
}
