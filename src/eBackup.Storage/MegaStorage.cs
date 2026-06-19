using CG.Web.MegaApiClient;
using eBackup.Core.Security;
using eBackup.Storage.Sftp; // RemoteFileInfo, ConnectionTestResult

namespace eBackup.Storage;

/// <summary>
/// Хранилище MEGA (mega.nz) на community-либе MegaApiClient. Папка приложения — в корне аккаунта
/// (config.RemoteDirectory, по умолчанию «eBackup»; поддержаны вложенные через «/»). Секрет —
/// пароль аккаунта (как у FTP/WebDAV; MEGA шифрует на своей стороне, своё E2E не пишем).
/// На каждую операцию — отдельный логин/логаут (служба под SYSTEM, без долгоживущей сессии).
/// Range-чтение не поддержано (MegaApiClient качает целиком) → НЕ ISeekableArchiveStorage:
/// удалённый браузер архива тянет файл целиком, как у FTP.
/// </summary>
public sealed class MegaStorage(SavedStorage config, ISecretProtector protector) : IArchiveStorage
{
    public string Name => config.Name;

    private string Email => config.Username
        ?? throw new InvalidOperationException("У MEGA-хранилища не задан e-mail.");

    private string Password => config.ProtectedPassword is null
        ? throw new InvalidOperationException("У MEGA-хранилища не задан пароль.")
        : protector.Unprotect(config.ProtectedPassword);

    private string FolderName => string.IsNullOrWhiteSpace(config.RemoteDirectory)
        ? "eBackup"
        : config.RemoteDirectory!.Trim().Trim('/');

    public async Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default)
    {
        var client = new MegaApiClient();
        await client.LoginAsync(Email, Password, null).ConfigureAwait(false);
        try
        {
            var (folder, nodes) = await EnsureFolderAsync(client, create: true).ConfigureAwait(false);
            // MEGA допускает одноимённые файлы в папке — старую копию убираем (перезалив, чистый листинг/retention).
            var dup = nodes.FirstOrDefault(n => n.Type == NodeType.File && n.ParentId == folder!.Id
                && string.Equals(n.Name, remoteName, StringComparison.OrdinalIgnoreCase));
            if (dup is not null)
                await client.DeleteAsync(dup, moveToTrash: false).ConfigureAwait(false);

            await using var stream = File.OpenRead(localFilePath);
            await client.UploadAsync(stream, remoteName, folder, progress: null, modificationDate: null, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        finally { await SafeLogoutAsync(client).ConfigureAwait(false); }
    }

    public async Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var client = new MegaApiClient();
        await client.LoginAsync(Email, Password, null).ConfigureAwait(false);
        try
        {
            var node = await FindFileAsync(client, remoteName).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"В MEGA не найден архив «{remoteName}».");
            await client.DownloadFileAsync(node, localFilePath, progress: null, cancellationToken: ct).ConfigureAwait(false);
        }
        finally { await SafeLogoutAsync(client).ConfigureAwait(false); }
    }

    public async Task<IReadOnlyList<RemoteFileInfo>> ListDetailedAsync(CancellationToken ct = default)
    {
        var client = new MegaApiClient();
        await client.LoginAsync(Email, Password, null).ConfigureAwait(false);
        try
        {
            var (folder, nodes) = await EnsureFolderAsync(client, create: false).ConfigureAwait(false);
            if (folder is null)
                return [];

            return nodes
                .Where(n => n.Type == NodeType.File && n.ParentId == folder.Id
                    && n.Name.EndsWith(".ebk", StringComparison.OrdinalIgnoreCase))
                .Select(n => new RemoteFileInfo(n.Name, n.Size,
                    (n.ModificationDate ?? n.CreationDate ?? DateTime.Now).ToLocalTime()))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
        }
        finally { await SafeLogoutAsync(client).ConfigureAwait(false); }
    }

    public async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        var client = new MegaApiClient();
        await client.LoginAsync(Email, Password, null).ConfigureAwait(false);
        try
        {
            var node = await FindFileAsync(client, remoteName).ConfigureAwait(false);
            if (node is not null)
                await client.DeleteAsync(node, moveToTrash: false).ConfigureAwait(false);
        }
        finally { await SafeLogoutAsync(client).ConfigureAwait(false); }
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
    {
        var client = new MegaApiClient();
        try
        {
            await client.LoginAsync(Email, Password, null).ConfigureAwait(false);
            await EnsureFolderAsync(client, create: true).ConfigureAwait(false);
            return ConnectionTestResult.Ok($"MEGA доступна, папка «{FolderName}».");
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Fail(ex.Message);
        }
        finally { await SafeLogoutAsync(client).ConfigureAwait(false); }
    }

    /// <summary>Найти узел-файл по имени в папке приложения (или null).</summary>
    private async Task<INode?> FindFileAsync(IMegaApiClient client, string remoteName)
    {
        var (folder, nodes) = await EnsureFolderAsync(client, create: false).ConfigureAwait(false);
        return folder is null ? null : nodes.FirstOrDefault(n => n.Type == NodeType.File && n.ParentId == folder.Id
            && string.Equals(n.Name, remoteName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Разрешить (опц. создать) папку приложения по сегментам. Возвращает узел папки (или null) + все узлы.</summary>
    private async Task<(INode? folder, List<INode> nodes)> EnsureFolderAsync(IMegaApiClient client, bool create)
    {
        var nodes = (await client.GetNodesAsync().ConfigureAwait(false)).ToList();
        var parent = nodes.First(n => n.Type == NodeType.Root);
        foreach (var segment in FolderName.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = nodes.FirstOrDefault(n => n.Type == NodeType.Directory && n.ParentId == parent.Id
                && string.Equals(n.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                if (!create)
                    return (null, nodes);
                child = await client.CreateFolderAsync(segment, parent).ConfigureAwait(false);
                nodes.Add(child);
            }
            parent = child;
        }
        return (parent, nodes);
    }

    private static async Task SafeLogoutAsync(IMegaApiClient client)
    {
        try { if (client.IsLoggedIn) await client.LogoutAsync().ConfigureAwait(false); } catch { /* best-effort */ }
    }
}
