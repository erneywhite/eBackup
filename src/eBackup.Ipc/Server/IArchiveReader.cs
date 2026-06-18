using eBackup.Ipc.Contracts;

namespace eBackup.Ipc.Server;

/// <summary>
/// Серверная seek-сессия чтения архива в хранилище службы: GUI читает удалённый .ebk «оглавлением
/// и кусками» (без полной закачки), а служба держит открытым <c>ISeekableArchiveStorage</c>-поток
/// по хэндлу. Реализация — в службе (у неё секрет хранилища под машинным ключом). Хэндлы привязаны
/// к соединению: <see cref="IpcConnection"/> закрывает все при обрыве.
/// </summary>
public interface IArchiveReader
{
    /// <summary>Открыть архив на seek-чтение → хэндл + полная длина.</summary>
    Task<OpenArchiveReadResponse> OpenAsync(OpenArchiveReadRequest req, CallerContext caller, CancellationToken ct);

    /// <summary>Прочитать кусок [offset, offset+count). Короткий ответ у конца файла — норма.</summary>
    Task<byte[]> ReadAsync(string handle, long offset, int count, CancellationToken ct);

    /// <summary>Закрыть сессию (освободить поток/соединение/временный файл). Идемпотентно.</summary>
    void Close(string handle);
}
