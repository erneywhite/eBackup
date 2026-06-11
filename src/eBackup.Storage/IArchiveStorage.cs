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
}
