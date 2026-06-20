using System.Collections.Concurrent;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using eBackup.Localization;
using eBackup.Storage;

namespace eBackup.Service;

/// <summary>
/// Серверная реализация seek-чтения архивов: открывает существующий <see cref="ISeekableArchiveStorage"/>
/// хранилища службы (секрет под машинным ключом) и держит поток по хэндлу, отдавая GUI куски по запросу
/// — браузер/восстановление читают удалённый .ebk без полной закачки. Папка читается напрямую,
/// неподдерживающие seek (FTP) — фолбэк на временную полную загрузку.
/// </summary>
public sealed class ServiceArchiveReader(StorageStore storages) : IArchiveReader
{
    private const int MaxChunk = 512 * 1024; // под лимит фрейма клиента (≈683 КБ base64 < 1 МиБ)

    private sealed class Session
    {
        public required Stream Stream { get; init; }
        public string? TempFile { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1); // seek+read атомарны на один хэндл
    }

    private readonly ConcurrentDictionary<string, Session> _open = new();

    public async Task<OpenArchiveReadResponse> OpenAsync(OpenArchiveReadRequest req, CallerContext caller, CancellationToken ct)
    {
        var saved = (await storages.LoadAsync(ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == req.StorageId)
            ?? throw new IpcFaultException(IpcErrorCodes.NotFound, L.Get("Svc_StorageNotFound"));
        var storage = StorageFactory.Create(saved, storages.Protector);

        Stream stream;
        string? temp = null;
        if (storage is ISeekableArchiveStorage seekable)
        {
            stream = await seekable.OpenSeekableReadAsync(req.RemoteName, ct).ConfigureAwait(false);
        }
        else if (storage is FolderStorage folder)
        {
            stream = File.OpenRead(folder.GetLocalPath(req.RemoteName)); // локальная папка — прямой seek
        }
        else
        {
            // FTP и т.п. — seek не умеют: тянем во временный файл целиком (как делал GUI до службы).
            temp = Path.Combine(Path.GetTempPath(), "eBackup", "read", Guid.NewGuid().ToString("N") + ".ebk");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            await storage.DownloadAsync(req.RemoteName, temp, ct).ConfigureAwait(false);
            stream = File.OpenRead(temp);
        }

        var handle = Guid.NewGuid().ToString("N");
        _open[handle] = new Session { Stream = stream, TempFile = temp };
        return new OpenArchiveReadResponse { Handle = handle, Length = stream.Length };
    }

    public async Task<byte[]> ReadAsync(string handle, long offset, int count, CancellationToken ct)
    {
        if (!_open.TryGetValue(handle, out var s))
            throw new IpcFaultException(IpcErrorCodes.NotFound, L.Get("Svc_ArchiveReadSessionNotFound"));

        count = Math.Clamp(count, 0, MaxChunk);
        if (count == 0) return [];

        await s.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            s.Stream.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = await s.Stream.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
                if (n == 0) break; // короткий хвост у конца файла — норма
                read += n;
            }
            return read == count ? buf : buf[..read];
        }
        finally
        {
            s.Gate.Release();
        }
    }

    public void Close(string handle)
    {
        if (!_open.TryRemove(handle, out var s)) return;
        try { s.Stream.Dispose(); } catch { }
        try { s.Gate.Dispose(); } catch { }
        if (s.TempFile is not null)
            try { File.Delete(s.TempFile); } catch { }
    }
}
