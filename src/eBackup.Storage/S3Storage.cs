using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using eBackup.Core.Security;
using eBackup.Storage.Sftp;

namespace eBackup.Storage;

/// <summary>
/// S3-совместимое хранилище (AWS S3, MinIO, Backblaze B2, Cloudflare R2, Яндекс и т.п.).
/// «Папка» в бакете — это префикс ключей (SavedStorage.RemoteDirectory).
/// </summary>
public sealed class S3Storage(SavedStorage config, ISecretProtector protector) : IArchiveStorage
{
    public string Name => config.Name;

    private string Bucket => config.Bucket
        ?? throw new InvalidOperationException("У S3-хранилища не задан бакет.");

    private string Prefix => NormalizePrefix(config.RemoteDirectory);

    /// <summary>«/backups/», «backups», «.» → «backups/»; пусто — корень бакета.</summary>
    internal static string NormalizePrefix(string? prefix)
    {
        var p = (prefix ?? string.Empty).Trim().Trim('/');
        if (p == ".")
            p = string.Empty;
        return p.Length == 0 ? string.Empty : p + "/";
    }

    private AmazonS3Client CreateClient()
    {
        var url = config.ServiceUrl?.Trim()
            ?? throw new InvalidOperationException("У S3-хранилища не задан endpoint.");
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        var secret = config.ProtectedSecretKey is null
            ? string.Empty
            : protector.Unprotect(config.ProtectedSecretKey);

        return new AmazonS3Client(
            new BasicAWSCredentials(config.AccessKeyId ?? string.Empty, secret),
            new AmazonS3Config
            {
                ServiceURL = url,
                ForcePathStyle = config.ForcePathStyle
            });
    }

    public async Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = Prefix + remoteName,
            FilePath = localFilePath
        }, ct).ConfigureAwait(false);
    }

    public async Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var client = CreateClient();
        using var response = await client.GetObjectAsync(Bucket, Prefix + remoteName, ct).ConfigureAwait(false);
        await response.WriteResponseStreamToFileAsync(localFilePath, append: false, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteFileInfo>> ListDetailedAsync(CancellationToken ct = default)
    {
        using var client = CreateClient();
        var result = new List<RemoteFileInfo>();
        string? token = null;

        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = Bucket,
                Prefix = Prefix,
                ContinuationToken = token
            }, ct).ConfigureAwait(false);

            foreach (var obj in response.S3Objects ?? [])
            {
                var name = obj.Key[Prefix.Length..];
                // Только файлы «этого уровня» с нашим расширением.
                if (name.Length == 0 || name.Contains('/') ||
                    !name.EndsWith(".ebk", StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new RemoteFileInfo(
                    name,
                    obj.Size ?? 0,
                    (obj.LastModified ?? DateTime.MinValue).ToLocalTime()));
            }

            token = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (token is not null);

        return result.OrderByDescending(f => f.LastWriteTime).ToList();
    }

    public async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await client.DeleteObjectAsync(Bucket, Prefix + remoteName, ct).ConfigureAwait(false);
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = Bucket,
                Prefix = Prefix,
                MaxKeys = 1
            }, ct).ConfigureAwait(false);
            return ConnectionTestResult.Ok($"Бакет «{Bucket}» доступен.");
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Fail(ex.Message);
        }
    }
}
