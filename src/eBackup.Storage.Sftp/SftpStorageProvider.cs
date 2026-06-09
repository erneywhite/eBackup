using eBackup.Core.Abstractions;

namespace eBackup.Storage.Sftp;

/// <summary>
/// Хранение архивов по SFTP.
/// Реализация на SSH.NET (Renci.SshNet) появится на этапе реализации v1;
/// учётные данные (host/port/login/пароль или ключ) будут храниться через Windows DPAPI.
/// </summary>
public sealed class SftpStorageProvider : IStorageProvider
{
    public string Name => "SFTP";

    public Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default)
        => throw new NotImplementedException("SFTP-провайдер будет реализован на SSH.NET.");

    public Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default)
        => throw new NotImplementedException("SFTP-провайдер будет реализован на SSH.NET.");

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
        => throw new NotImplementedException("SFTP-провайдер будет реализован на SSH.NET.");
}
