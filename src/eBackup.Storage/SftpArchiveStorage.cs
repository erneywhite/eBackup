using eBackup.Storage.Sftp;

namespace eBackup.Storage;

/// <summary>Адаптер SFTP-провайдера под единую поверхность хранилищ.</summary>
public sealed class SftpArchiveStorage(SftpStorageProvider provider, string name) : IArchiveStorage
{
    public string Name => name;

    public Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default)
        => provider.UploadAsync(localFilePath, remoteName, ct);

    public Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default)
        => provider.DownloadAsync(remoteName, localFilePath, ct);

    public Task<IReadOnlyList<RemoteFileInfo>> ListDetailedAsync(CancellationToken ct = default)
        => provider.ListDetailedAsync(ct);

    public Task DeleteAsync(string remoteName, CancellationToken ct = default)
        => provider.DeleteAsync(remoteName, ct);

    public Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
        => provider.TestConnectionAsync(ct);
}
