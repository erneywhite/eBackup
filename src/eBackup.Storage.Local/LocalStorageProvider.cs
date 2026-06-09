using eBackup.Core.Abstractions;

namespace eBackup.Storage.Local;

/// <summary>
/// Хранит архивы в локальной или примонтированной сетевой папке.
/// </summary>
public sealed class LocalStorageProvider : IStorageProvider
{
    private readonly string _root;

    public LocalStorageProvider(string rootDirectory)
    {
        _root = rootDirectory;
        Directory.CreateDirectory(_root);
    }

    public string Name => $"Local ({_root})";

    public Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default)
    {
        File.Copy(localFilePath, Path.Combine(_root, remoteName), overwrite: true);
        return Task.CompletedTask;
    }

    public Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default)
    {
        File.Copy(Path.Combine(_root, remoteName), localFilePath, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> files = Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*.ebk")
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .ToList()
            : [];

        return Task.FromResult(files);
    }
}
