using System.Text.Json;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Transport;

namespace eBackup.Ipc.Server;

/// <summary>
/// Обслуживает ОДНО соединение. Дуплекс: цикл чтения обрабатывает запросы (req→resp через
/// <see cref="IpcDispatcher"/>), а AttachToJob поднимает фоновый «насос», который шлёт клиенту
/// note-фреймы задачи (бэклог + живые) через тот же сериализованный writer, не блокируя приём
/// других запросов. Любой сбой/закрытие завершают цикл и гасят все насосы.
/// </summary>
public static class IpcConnection
{
    public const int MaxInboundFrameBytes = 256 * 1024;

    public static async Task ServeAsync(
        Stream stream, IIpcHandlers handlers, Func<CallerContext> resolveCaller,
        CancellationToken ct, IJobStream? jobStream = null)
    {
        var reader = new FrameReader(stream, MaxInboundFrameBytes);
        using var writer = new FrameWriter(stream);

        // Асимметрия рукопожатия (см. S4b): клиент пишет преамбулу первым, сервер читает первым.
        await reader.ReadPreambleAsync(ct).ConfigureAwait(false);
        var caller = resolveCaller();
        await writer.WritePreambleAsync(ct).ConfigureAwait(false);

        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumps = new Dictionary<string, CancellationTokenSource>();

        try
        {
            while (!connCts.IsCancellationRequested)
            {
                Frame? req;
                try { req = await reader.ReadFrameAsync(connCts.Token).ConfigureAwait(false); }
                catch (IpcProtocolException) { break; }
                if (req is null) break;

                if (req.Kind == FrameKinds.Req && req.Op == IpcOps.AttachToJob)
                    await HandleAttachAsync(req, writer, jobStream, caller, pumps, connCts.Token).ConfigureAwait(false);
                else if (req.Kind == FrameKinds.Req && req.Op == IpcOps.DetachFromJob)
                    await HandleDetachAsync(req, writer, pumps, connCts.Token).ConfigureAwait(false);
                else
                    await writer.WriteFrameAsync(
                        await IpcDispatcher.DispatchAsync(req, handlers, caller, connCts.Token).ConfigureAwait(false),
                        connCts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            connCts.Cancel(); // погасить все насосы
            lock (pumps)
            {
                foreach (var p in pumps.Values) { try { p.Cancel(); p.Dispose(); } catch { } }
                pumps.Clear();
            }
        }
    }

    private static async Task HandleAttachAsync(
        Frame req, FrameWriter writer, IJobStream? jobStream, CallerContext caller,
        Dictionary<string, CancellationTokenSource> pumps, CancellationToken ct)
    {
        var attach = req.Body?.Deserialize(IpcJsonContext.Default.AttachRequest);
        if (jobStream is null || attach is null)
        {
            await writer.WriteFrameAsync(IpcDispatcher.Fault(req.Id, IpcErrorCodes.Unsupported, "Стриминг недоступен."), ct).ConfigureAwait(false);
            return;
        }

        var sub = jobStream.Attach(attach.JobId, attach.FromSeq, caller);
        if (sub is null)
        {
            await writer.WriteFrameAsync(IpcDispatcher.Fault(req.Id, IpcErrorCodes.NotFound, "Задача не найдена."), ct).ConfigureAwait(false);
            return;
        }

        var ack = new AttachAck
        {
            Accepted = true,
            LowestRetainedSeq = sub.LowestRetainedSeq,
            NextSeq = sub.NextSeq,
            Finished = sub.Finished,
        };
        await writer.WriteFrameAsync(IpcDispatcher.Resp(req.Id, ack, IpcJsonContext.Default.AttachAck), ct).ConfigureAwait(false);

        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (pumps)
        {
            if (pumps.Remove(attach.JobId, out var old)) { try { old.Cancel(); old.Dispose(); } catch { } }
            pumps[attach.JobId] = pumpCts;
        }
        _ = PumpAsync(sub, writer, pumpCts.Token);
    }

    private static async Task PumpAsync(JobSubscription sub, FrameWriter writer, CancellationToken ct)
    {
        try
        {
            foreach (var f in sub.Backlog)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteFrameAsync(f, ct).ConfigureAwait(false);
            }
            await foreach (var f in sub.Live.WithCancellation(ct).ConfigureAwait(false))
                await writer.WriteFrameAsync(f, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* соединение упало — read loop разрулит */ }
        finally { try { sub.Detach(); } catch { } }
    }

    private static async Task HandleDetachAsync(
        Frame req, FrameWriter writer, Dictionary<string, CancellationTokenSource> pumps, CancellationToken ct)
    {
        var d = req.Body?.Deserialize(IpcJsonContext.Default.DetachRequest);
        if (d is not null)
            lock (pumps)
            {
                if (pumps.Remove(d.JobId, out var cts)) { try { cts.Cancel(); cts.Dispose(); } catch { } }
            }
        await writer.WriteFrameAsync(IpcDispatcher.Resp(req.Id, new Ack(), IpcJsonContext.Default.Ack), ct).ConfigureAwait(false);
    }
}
