using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
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
    private readonly ConcurrentDictionary<string, Channel<Frame>> _noteSubs = new(); // jobId → живой поток нот
    private readonly CancellationTokenSource _cts = new();
    private static readonly HashSet<string> TerminalStates = ["Completed", "CompletedWithErrors", "Failed", "Cancelled"];
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
                else if (frame.Kind == FrameKinds.Note && frame.Body is { } body
                    && body.TryGetProperty("jobId", out var jid) && jid.GetString() is { } jobId
                    && _noteSubs.TryGetValue(jobId, out var noteCh))
                {
                    noteCh.Writer.TryWrite(frame);
                }
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

    public Task<TResp> RequestAsync<TReq, TResp>(
        string op, TReq req, JsonTypeInfo<TReq> reqTi, JsonTypeInfo<TResp> respTi, CancellationToken ct = default)
        => CallAsync(op, JsonSerializer.SerializeToElement(req, reqTi), respTi, ct);

    /// <summary>Запрос без тела (для операций-списков).</summary>
    public Task<TResp> RequestNoBodyAsync<TResp>(string op, JsonTypeInfo<TResp> respTi, CancellationToken ct = default)
        => CallAsync(op, null, respTi, ct);

    private async Task<TResp> CallAsync<TResp>(string op, JsonElement? body, JsonTypeInfo<TResp> respTi, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _idCounter).ToString();
        var tcs = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var frame = new Frame { Kind = FrameKinds.Req, Id = id, Op = op, Body = body };

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

    public Task<JobStatus[]> ListJobsAsync(bool includeFinished, CancellationToken ct = default)
        => RequestAsync(IpcOps.ListJobs, new ListJobsRequest { IncludeFinished = includeFinished }, Ctx.ListJobsRequest, Ctx.JobStatusArray, ct);

    public Task<Ack> CancelJobAsync(string jobId, CancellationToken ct = default)
        => RequestAsync(IpcOps.CancelJob, new CancelJobRequest { JobId = jobId }, Ctx.CancelJobRequest, Ctx.Ack, ct);

    public Task<StorageSummary[]> ListStoragesAsync(CancellationToken ct = default)
        => RequestNoBodyAsync(IpcOps.ListStorages, Ctx.StorageSummaryArray, ct);

    public Task<StorageDetail[]> ListStorageDetailsAsync(CancellationToken ct = default)
        => RequestNoBodyAsync(IpcOps.ListStorageDetails, Ctx.StorageDetailArray, ct);

    public Task<StorageDetail> GetStorageAsync(string id, CancellationToken ct = default)
        => RequestAsync(IpcOps.GetStorage, new GetStorageRequest { Id = id }, Ctx.GetStorageRequest, Ctx.StorageDetail, ct);

    public Task<Ack> UpsertStorageAsync(StorageInput input, CancellationToken ct = default)
        => RequestAsync(IpcOps.UpsertStorage, input, Ctx.StorageInput, Ctx.Ack, ct);

    public Task<Ack> DeleteStorageAsync(string id, CancellationToken ct = default)
        => RequestAsync(IpcOps.DeleteStorage, new DeleteByIdRequest { Id = id }, Ctx.DeleteByIdRequest, Ctx.Ack, ct);

    public Task<TestResult> TestStorageAsync(TestStorageRequest req, CancellationToken ct = default)
        => RequestAsync(IpcOps.TestStorage, req, Ctx.TestStorageRequest, Ctx.TestResult, ct);

    public Task<RemoteFileDto[]> ListArchivesAsync(string storageId, CancellationToken ct = default)
        => RequestAsync(IpcOps.ListArchives, new ListArchivesRequest { StorageId = storageId }, Ctx.ListArchivesRequest, Ctx.RemoteFileDtoArray, ct);

    public Task<OpenArchiveReadResponse> OpenArchiveReadAsync(string storageId, string remoteName, CancellationToken ct = default)
        => RequestAsync(IpcOps.OpenArchiveRead, new OpenArchiveReadRequest { StorageId = storageId, RemoteName = remoteName }, Ctx.OpenArchiveReadRequest, Ctx.OpenArchiveReadResponse, ct);

    public async Task<byte[]> ReadArchiveChunkAsync(string handle, long offset, int count, CancellationToken ct = default)
    {
        var resp = await RequestAsync(IpcOps.ReadArchiveChunk,
            new ReadArchiveChunkRequest { Handle = handle, Offset = offset, Count = count },
            Ctx.ReadArchiveChunkRequest, Ctx.ReadArchiveChunkResponse, ct).ConfigureAwait(false);
        return resp.Data;
    }

    public Task<Ack> CloseArchiveReadAsync(string handle, CancellationToken ct = default)
        => RequestAsync(IpcOps.CloseArchiveRead, new CloseArchiveReadRequest { Handle = handle }, Ctx.CloseArchiveReadRequest, Ctx.Ack, ct);

    public Task<ModuleSummary[]> ListModulesAsync(CancellationToken ct = default)
        => RequestNoBodyAsync(IpcOps.ListModules, Ctx.ModuleSummaryArray, ct);

    public Task<string[]> ListCustomFoldersAsync(CancellationToken ct = default)
        => RequestNoBodyAsync(IpcOps.ListCustomFolders, Ctx.StringArray, ct);

    public Task<Ack> UpsertCustomFolderAsync(string path, CancellationToken ct = default)
        => RequestAsync(IpcOps.UpsertCustomFolder, new UpsertCustomFolderRequest { Path = path }, Ctx.UpsertCustomFolderRequest, Ctx.Ack, ct);

    public Task<Ack> DeleteCustomFolderAsync(string path, CancellationToken ct = default)
        => RequestAsync(IpcOps.DeleteCustomFolder, new DeleteByIdRequest { Id = path }, Ctx.DeleteByIdRequest, Ctx.Ack, ct);

    public Task<BackupRunRecordDto[]> ListHistoryAsync(int limit = 100, CancellationToken ct = default)
        => RequestAsync(IpcOps.ListHistory, new ListHistoryRequest { Limit = limit }, Ctx.ListHistoryRequest, Ctx.BackupRunRecordDtoArray, ct);

    public Task<GetRunLogResponse> GetRunLogAsync(string runId, long fromSeq, int maxLines, CancellationToken ct = default)
        => RequestAsync(IpcOps.GetRunLog, new GetRunLogRequest { RunId = runId, FromSeq = fromSeq, MaxLines = maxLines }, Ctx.GetRunLogRequest, Ctx.GetRunLogResponse, ct);

    /// <summary>
    /// Подписаться на живой прогресс задачи: отдаёт note-фреймы (Phase/Log/State) — бэклог,
    /// затем живые — и завершается, когда придёт нота терминального состояния. Op фрейма —
    /// тип ноты (<see cref="IpcNotes"/>), тело декодируется вызывающим по op.
    /// </summary>
    public async IAsyncEnumerable<Frame> AttachToJobAsync(
        string jobId, long fromSeq = 0, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ch = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true });
        _noteSubs[jobId] = ch;
        try
        {
            // Регистрируем канал ДО запроса — ноты, пришедшие сразу после AttachAck, не потеряются.
            await RequestAsync(IpcOps.AttachToJob, new AttachRequest { JobId = jobId, FromSeq = fromSeq },
                Ctx.AttachRequest, Ctx.AttachAck, ct).ConfigureAwait(false);

            await foreach (var f in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return f;
                if (IsTerminalNote(f)) yield break; // задача завершилась — поток окончен
            }
        }
        finally
        {
            _noteSubs.TryRemove(jobId, out _);
            _ = DetachQuietlyAsync(jobId);
        }
    }

    private async Task DetachQuietlyAsync(string jobId)
    {
        try
        {
            await RequestAsync(IpcOps.DetachFromJob, new DetachRequest { JobId = jobId },
                Ctx.DetachRequest, Ctx.Ack, _cts.Token).ConfigureAwait(false);
        }
        catch { /* best-effort: сервер всё равно погасит насос при закрытии соединения */ }
    }

    private static bool IsTerminalNote(Frame f)
        => f.Op == IpcNotes.State && f.Body is { } b
           && b.TryGetProperty("state", out var s) && TerminalStates.Contains(s.GetString() ?? "");

    public void Dispose()
    {
        if (_disposed) return; // идемпотентно — безопасно звать повторно (явно + через using)
        _disposed = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _writer.Dispose();
        _cts.Dispose();
    }
}
