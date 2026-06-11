using System.Runtime.InteropServices;
using eBackup.Core.Security;
using eBackup.Storage.Sftp;

namespace eBackup.Storage;

/// <summary>
/// Хранилище-папка: локальный путь, прокинутый сетевой диск или UNC-шара.
/// Если заданы учётные данные SMB — поднимает ВРЕМЕННОЕ подключение через
/// WNetAddConnection2 (в системе не сохраняется, исчезает при выходе из сессии).
/// </summary>
public sealed class FolderStorage(SavedStorage config, ISecretProtector protector) : IArchiveStorage
{
    private string Root => config.Path
        ?? throw new InvalidOperationException("У хранилища-папки не задан путь.");

    public string Name => config.Name;

    public Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default)
        => Task.Run(() =>
        {
            EnsureConnected();
            Directory.CreateDirectory(Root);
            File.Copy(localFilePath, System.IO.Path.Combine(Root, remoteName), overwrite: true);
        }, ct);

    public Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default)
        => Task.Run(() =>
        {
            EnsureConnected();
            var dir = System.IO.Path.GetDirectoryName(localFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.Copy(System.IO.Path.Combine(Root, remoteName), localFilePath, overwrite: true);
        }, ct);

    public Task<IReadOnlyList<RemoteFileInfo>> ListDetailedAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<RemoteFileInfo>>(() =>
        {
            EnsureConnected();
            if (!Directory.Exists(Root))
                return [];
            return Directory.GetFiles(Root, "*.ebk")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new RemoteFileInfo(f.Name, f.Length, f.LastWriteTime))
                .ToList();
        }, ct);

    public Task DeleteAsync(string remoteName, CancellationToken ct = default)
        => Task.Run(() =>
        {
            EnsureConnected();
            File.Delete(System.IO.Path.Combine(Root, remoteName));
        }, ct);

    public Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                EnsureConnected();
                Directory.CreateDirectory(Root);
                // Проба записи: доступность ≠ права на запись.
                var probe = System.IO.Path.Combine(Root, $".ebk-probe-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return ConnectionTestResult.Ok($"Папка доступна для записи: {Root}");
            }
            catch (Exception ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }, ct);

    /// <summary>Полный путь файла в хранилище (для прямого восстановления без копирования).</summary>
    public string GetLocalPath(string remoteName) => System.IO.Path.Combine(Root, remoteName);

    // ---------- SMB-подключение с явными учётными данными ----------

    private void EnsureConnected()
    {
        if (string.IsNullOrEmpty(config.ProtectedSharePassword) ||
            !Root.StartsWith(@"\\", StringComparison.Ordinal))
            return;

        var share = GetShareRoot(Root);
        if (share is null)
            return;

        var password = protector.Unprotect(config.ProtectedSharePassword);
        var resource = new NETRESOURCE { dwType = RESOURCETYPE_DISK, lpRemoteName = share };
        var rc = WNetAddConnection2(ref resource, password,
            string.IsNullOrWhiteSpace(config.ShareUsername) ? null : config.ShareUsername,
            0 /* временное подключение, не сохраняется */);

        // 1219 — к серверу уже есть сессия с другими учётными данными; работаем через неё.
        if (rc != 0 && rc != 1219)
            throw new IOException($"Не удалось подключиться к {share} (Win32 код {rc}).");
    }

    /// <summary>«\\server\share\sub\dir» → «\\server\share».</summary>
    internal static string? GetShareRoot(string uncPath)
    {
        var parts = uncPath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : null;
    }

    private const int RESOURCETYPE_DISK = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NETRESOURCE lpNetResource,
        string? lpPassword, string? lpUserName, int dwFlags);
}
