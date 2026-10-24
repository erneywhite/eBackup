using System.Text.Json;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Transport;

namespace eBackup.Ipc.Server;

/// <summary>
/// Обслуживает ОДНО соединение. Дуплекс: цикл чтения принимает запросы и запускает их ПАРАЛЛЕЛЬНО
/// (req→resp через <see cref="IpcDispatcher"/>), а AttachToJob поднимает фоновый «насос», который шлёт
/// клиенту note-фреймы задачи (бэклог + живые). Все ответы идут через один сериализованный writer.
/// Любой сбой/закрытие завершают цикл и гасят все насосы.
/// <para>
/// Почему параллельно: у GUI одно соединение на все вкладки. При последовательной обработке медленный
/// запрос (листинг облака, чтение архива) держал в очереди всё остальное — вкладки «пустели», пока
/// не ответит MEGA. Writer (семафор) и состояние соединения (насосы, хэндлы под lock) потокобезопасны,
/// чтение одного архивного хэндла сериализует сам IArchiveReader.
/// </para>
/// </summary>
public static class IpcConnection
{
    public const int MaxInboundFrameBytes = 256 * 1024;

    /// <summary>Сколько запросов одного соединения выполняется одновременно (дальше — ждут слот).</summary>
    public const int MaxConcurrentRequests = 16;

    public static async Task ServeAsync(
        Stream stream, IIpcHandlers handlers, Func<CallerContext> resolveCaller,
        CancellationToken ct, IJobStream? jobStream = null, IArchiveReader? archiveReader = null)
    {
        var reader = new FrameReader(stream, MaxInboundFrameBytes);
        using var writer = new FrameWriter(stream);

        // Асимметрия рукопожатия (см. S4b): клиент пишет преамбулу первым, сервер читает первым.
        await reader.ReadPreambleAsync(ct).ConfigureAwait(false);
        var caller = resolveCaller();
        await writer.WritePreambleAsync(ct).ConfigureAwait(false);

        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumps = new Dictionary<string, CancellationTokenSource>();
        var archiveHandles = new HashSet<string>(); // seek-сессии чтения архива этого соединения
        using var slots = new SemaphoreSlim(MaxConcurrentRequests);
        var inflight = new List<Task>();

        async Task RunOneAsync(Frame req)
        {
            try
            {
                if (req.Kind == FrameKinds.Req && req.Op == IpcOps.AttachToJob)
                    await HandleAttachAsync(req, writer, jobStream, caller, pumps, connCts.Token).ConfigureAwait(false);
                else if (req.Kind == FrameKinds.Req && req.Op == IpcOps.DetachFromJob)
                    await HandleDetachAsync(req, writer, pumps, connCts.Token).ConfigureAwait(false);
                else if (req.Kind == FrameKinds.Req && req.Op == IpcOps.OpenArchiveRead)
                    await HandleOpenArchiveAsync(req, writer, archiveReader, caller, archiveHandles, connCts.Token).ConfigureAwait(false);
                else if (req.Kind == FrameKinds.Req && req.Op == IpcOps.ReadArchiveChunk)
                    await HandleReadChunkAsync(req, writer, archiveReader, connCts.Token).ConfigureAwait(false);
                else if (req.Kind == FrameKinds.Req && req.Op == IpcOps.CloseArchiveRead)
                    await HandleCloseArchiveAsync(req, writer, archiveReader, archiveHandles, connCts.Token).ConfigureAwait(false);
                else
                    await WriteResponseAsync(req, writer,
                        await IpcDispatcher.DispatchAsync(req, handlers, caller, connCts.Token).ConfigureAwait(false),
                        connCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* соединение закрывается */ }
            catch (Exception) { /* pipe упал — цикл чтения увидит это сам и завершит соединение */ }
            finally { slots.Release(); }
        }

        try
        {
            while (!connCts.IsCancellationRequested)
            {
                Frame? req;
                try { req = await reader.ReadFrameAsync(connCts.Token).ConfigureAwait(false); }
                catch (IpcProtocolException) { break; }
                if (req is null) break;

                await slots.WaitAsync(connCts.Token).ConfigureAwait(false); // обратное давление при перегрузе
                lock (inflight)
                {
                    inflight.RemoveAll(t => t.IsCompleted);
                    inflight.Add(RunOneAsync(req));
                }
            }
        }
        finally
        {
            connCts.Cancel(); // погасить все насосы и незавершённые запросы
            Task[] pending;
            lock (inflight) pending = [.. inflight];
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { } // до освобождения writer/slots
            lock (pumps)
            {
                foreach (var p in pumps.Values) { try { p.Cancel(); p.Dispose(); } catch { } }
                pumps.Clear();
            }
            if (archiveReader is not null)
                lock (archiveHandles)
                {
                    foreach (var h in archiveHandles) { try { archiveReader.Close(h); } catch { } }
                    archiveHandles.Clear();
                }
        }
    }

    /// <summary>
    /// Ответ на обычный запрос. Слишком большой ответ (выше лимита фрейма) — это ошибка ЭТОГО запроса,
    /// а не повод рвать соединение: writer проверяет размер до записи, так что в pipe ещё ничего не ушло.
    /// </summary>
    private static async Task WriteResponseAsync(Frame req, FrameWriter writer, Frame resp, CancellationToken ct)
    {
        try
        {
            await writer.WriteFrameAsync(resp, ct).ConfigureAwait(false);
        }
        catch (IpcProtocolException)
        {
            await writer.WriteFrameAsync(IpcDispatcher.Fault(req.Id, IpcErrorCodes.Internal,
                "Ответ службы слишком большой — запрос нужно разбить на страницы."), ct).ConfigureAwait(false);
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

    // ---- seek-чтение архива (по образцу AttachToJob: спец-обработка + per-connection хэндлы) ----

    private static async Task HandleOpenArchiveAsync(
        Frame req, FrameWriter writer, IArchiveReader? reader, CallerContext caller,
        HashSet<string> handles, CancellationToken ct)
    {
        var r = req.Body?.Deserialize(IpcJsonContext.Default.OpenArchiveReadRequest);
        if (reader is null || r is null)
        {
            await writer.WriteFrameAsync(IpcDispatcher.Fault(req.Id, IpcErrorCodes.Unsupported, "Чтение архивов недоступно."), ct).ConfigureAwait(false);
            return;
        }
        try
        {
            var resp = await reader.OpenAsync(r, caller, ct).ConfigureAwait(false);
            lock (handles) handles.Add(resp.Handle);
            await writer.WriteFrameAsync(IpcDispatcher.Resp(req.Id, resp, IpcJsonContext.Default.OpenArchiveReadResponse), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (IpcFaultException fe) { await writer.WriteFrameAsync(IpcDispatcher.Fault(req.Id, fe.Code, fe.Message), ct).ConfigureAwait(false); }
        catch (Exception) { await writer.WriteFrameAsync(IpcDispatcher.Fault(req.Id, IpcErrorCodes.Internal, "Не удалось открыть архив."), ct).ConfigureAwait(false); }
    }

    private static async Task HandleReadChunkAsync(
        Frame req, FrameWriter writer, IArchiveReader? reader, CancellationToken ct)
    {
        var r = req.Body?.Deserialize(IpcJsonContext.Default.ReadArchiveChunkRequest);
        if (reader is null || r is null)
        {
            await writer.WriteFrameAsync(IpcDispatcher.Fault(req.Id, IpcErrorCodes.Unsupported, "Чтение архивов недоступно."), ct).ConfigureAwait(false);
            return;
        }
        try
        {
            var data = await reader.ReadAsync(r.Handle, r.Offset, r.Count, ct).ConfigureAwait(false);
            await writer.WriteFrameAsync(IpcDispatcher.Resp(req.Id, new ReadArchiveChunkResponse { Data = data }, IpcJsonContext.Default.ReadArchiveChunkResponse), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (IpcFaultException fe) { await writer.WriteFrameAsync(IpcDispatcher.Fault(req.Id, fe.Code, fe.Message), ct).ConfigureAwait(false); }
        catch (Exception) { await writer.WriteFrameAsync(IpcDispatcher.Fault(req.Id, IpcErrorCodes.Internal, "Ошибка чтения архива."), ct).ConfigureAwait(false); }
    }

    private static async Task HandleCloseArchiveAsync(
        Frame req, FrameWriter writer, IArchiveReader? reader, HashSet<string> handles, CancellationToken ct)
    {
        var r = req.Body?.Deserialize(IpcJsonContext.Default.CloseArchiveReadRequest);
        if (r is not null && reader is not null)
        {
            reader.Close(r.Handle);
            lock (handles) handles.Remove(r.Handle);
        }
        await writer.WriteFrameAsync(IpcDispatcher.Resp(req.Id, new Ack(), IpcJsonContext.Default.Ack), ct).ConfigureAwait(false);
    }
}
