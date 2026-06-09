namespace eBackup.Core.Abstractions;

/// <summary>
/// Назначение хранения готового архива: локальная папка, сетевой диск, SFTP/FTP,
/// облако и т.д. Можно подключать несколько провайдеров одновременно.
/// </summary>
public interface IStorageProvider
{
    /// <summary>Человекочитаемое имя провайдера (для логов и UI).</summary>
    string Name { get; }

    /// <summary>Загрузить локальный файл архива в хранилище под именем <paramref name="remoteName"/>.</summary>
    Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default);

    /// <summary>Скачать архив <paramref name="remoteName"/> в локальный путь.</summary>
    Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default);

    /// <summary>Перечислить доступные архивы в хранилище.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default);
}
