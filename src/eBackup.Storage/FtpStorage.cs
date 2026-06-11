using eBackup.Core.Security;
using eBackup.Storage.Sftp;
using FluentFTP;

namespace eBackup.Storage;

/// <summary>
/// Хранилище FTP / FTPS (FluentFTP). На каждую операцию — отдельное подключение:
/// просто и надёжно для нечастых операций бэкапа. FTPS по умолчанию проверяет
/// сертификат по системным корням; приём непроверенного — отдельным флагом.
/// </summary>
public sealed class FtpStorage(SavedStorage config, ISecretProtector protector) : IArchiveStorage
{
    public string Name => config.Name;

    private string Dir => string.IsNullOrWhiteSpace(config.RemoteDirectory) || config.RemoteDirectory == "."
        ? ""
        : config.RemoteDirectory!.TrimEnd('/');

    private string RemotePath(string name) => Dir.Length == 0 ? name : $"{Dir}/{name}";

    private async Task<AsyncFtpClient> ConnectAsync(CancellationToken ct)
    {
        var password = config.ProtectedPassword is null
            ? string.Empty
            : protector.Unprotect(config.ProtectedPassword);

        var client = new AsyncFtpClient(
            config.Host ?? throw new InvalidOperationException("У FTP-хранилища не задан хост."),
            config.Username ?? string.Empty,
            password,
            config.Port);

        if (config.UseFtps)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            // По умолчанию сертификат проверяется по системным корням (FluentFTP).
            // «Любой сертификат» — только по явному выбору (NAS с самоподписанным),
            // иначе FTPS-шифрование молча открыто для MITM.
            if (config.AllowUntrustedCertificate)
                client.Config.ValidateAnyCertificate = true;
        }

        await client.Connect(ct).ConfigureAwait(false);
        return client;
    }

    public async Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(ct).ConfigureAwait(false);
        var status = await client.UploadFile(localFilePath, RemotePath(remoteName),
            FtpRemoteExists.Overwrite, createRemoteDir: true, token: ct).ConfigureAwait(false);
        if (status == FtpStatus.Failed)
            throw new IOException($"FTP: не удалось загрузить {remoteName}.");
    }

    public async Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var client = await ConnectAsync(ct).ConfigureAwait(false);
        var status = await client.DownloadFile(localFilePath, RemotePath(remoteName),
            FtpLocalExists.Overwrite, token: ct).ConfigureAwait(false);
        if (status == FtpStatus.Failed)
            throw new IOException($"FTP: не удалось скачать {remoteName}.");
    }

    public async Task<IReadOnlyList<RemoteFileInfo>> ListDetailedAsync(CancellationToken ct = default)
    {
        using var client = await ConnectAsync(ct).ConfigureAwait(false);
        var listing = await client.GetListing(Dir.Length == 0 ? null : Dir, ct).ConfigureAwait(false);
        return listing
            .Where(i => i.Type == FtpObjectType.File &&
                        i.Name.EndsWith(".ebk", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Modified)
            .Select(i => new RemoteFileInfo(i.Name, i.Size, i.Modified.ToLocalTime()))
            .ToList();
    }

    public async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(ct).ConfigureAwait(false);
        await client.DeleteFile(RemotePath(remoteName), ct).ConfigureAwait(false);
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = await ConnectAsync(ct).ConfigureAwait(false);
            if (Dir.Length > 0 && !await client.DirectoryExists(Dir, ct).ConfigureAwait(false))
                await client.CreateDirectory(Dir, ct).ConfigureAwait(false);
            return ConnectionTestResult.Ok($"Подключение успешно ({(config.UseFtps ? "FTPS" : "FTP")}).");
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Fail(ex.Message);
        }
    }
}
