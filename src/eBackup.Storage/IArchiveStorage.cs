using eBackup.Storage.Sftp;

namespace eBackup.Storage;

/// <summary>
/// Единая поверхность хранилища архивов для приложения: заливка, скачивание,
/// листинг с атрибутами, удаление и проверка доступности — одинаково для папки,
/// SFTP и будущих облаков. (Не контракт плагинов — внутренний интерфейс хранилищ.)
/// </summary>
public interface IArchiveStorage
{
    string Name { get; }

    Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default);
    Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default);
    Task<IReadOnlyList<RemoteFileInfo>> ListDetailedAsync(CancellationToken ct = default);
    Task DeleteAsync(string remoteName, CancellationToken ct = default);
    Task<ConnectionTestResult> TestAsync(CancellationToken ct = default);

    /// <summary>
    /// Зашифрован ли архив (сигнатура EBKE) — для пометки 🔒 в списке. Читает только голову файла,
    /// без полной закачки. Дефолт умеет seek-хранилища; папка/MEGA/FTP переопределяют своим лёгким
    /// чтением. Возвращает false, если дёшево не определить (метка просто не покажется).
    /// </summary>
    async Task<bool> IsArchiveEncryptedAsync(string remoteName, CancellationToken ct = default)
    {
        if (this is ISeekableArchiveStorage seekable)
        {
            await using var s = await seekable.OpenSeekableReadAsync(remoteName, ct).ConfigureAwait(false);
            return await ArchiveHead.IsEbkeAsync(s, ct).ConfigureAwait(false);
        }
        return false;
    }
}
