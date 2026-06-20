using CG.Web.MegaApiClient;
using eBackup.Core.Security;
using eBackup.Localization;
using eBackup.Storage.Sftp; // RemoteFileInfo, ConnectionTestResult

namespace eBackup.Storage;

/// <summary>
/// Хранилище MEGA (mega.nz) на community-либе MegaApiClient. Аутентификация — по СОХРАНЁННОЙ СЕССИИ
/// (config.ProtectedOAuthToken): пользователь один раз вошёл с паролем и 2FA-кодом в редакторе (см.
/// <see cref="MegaSession"/>), дальше служба ходит по сессии без пароля/2FA — в т.ч. по расписанию.
/// Папка приложения в корне аккаунта (RemoteDirectory, дефолт «eBackup»; вложенные через «/»).
/// На каждую операцию — отдельный клиент + LoginAsync(сессия); БЕЗ logout (он убил бы сессию).
/// НЕ seekable (MegaApiClient качает целиком) → удалённый браузер тянет файл целиком, как у FTP.
/// </summary>
public sealed class MegaStorage(SavedStorage config, ISecretProtector protector) : IArchiveStorage
{
    public string Name => config.Name;

    private string FolderName => string.IsNullOrWhiteSpace(config.RemoteDirectory)
        ? "eBackup"
        : config.RemoteDirectory!.Trim().Trim('/');

    /// <summary>Новый клиент, вошедший по сохранённой сессии (без пароля/2FA, без logout).</summary>
    private async Task<MegaApiClient> LoginAsync()
    {
        if (config.ProtectedOAuthToken is null)
            throw new InvalidOperationException(L.Get("Stg_MegaNotConnected"));
        var token = MegaSession.Deserialize(protector.Unprotect(config.ProtectedOAuthToken));
        var client = new MegaApiClient();
        await client.LoginAsync(token).ConfigureAwait(false);
        return client;
    }

    public async Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default)
    {
        var client = await LoginAsync().ConfigureAwait(false);
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

    public async Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var client = await LoginAsync().ConfigureAwait(false);
        var node = await FindFileAsync(client, remoteName).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"В MEGA не найден архив «{remoteName}».");
        await client.DownloadFileAsync(node, localFilePath, progress: null, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteFileInfo>> ListDetailedAsync(CancellationToken ct = default)
    {
        var client = await LoginAsync().ConfigureAwait(false);
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

    public async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        var client = await LoginAsync().ConfigureAwait(false);
        var node = await FindFileAsync(client, remoteName).ConfigureAwait(false);
        if (node is not null)
            await client.DeleteAsync(node, moveToTrash: false).ConfigureAwait(false);
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
    {
        try
        {
            var client = await LoginAsync().ConfigureAwait(false);
            await EnsureFolderAsync(client, create: true).ConfigureAwait(false);
            return ConnectionTestResult.Ok(L.Get("Stg_MegaOk", FolderName));
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Fail(ex.Message);
        }
    }

    public async Task<bool> IsArchiveEncryptedAsync(string remoteName, CancellationToken ct = default)
    {
        var client = await LoginAsync().ConfigureAwait(false);
        var node = await FindFileAsync(client, remoteName).ConfigureAwait(false);
        if (node is null)
            return false;
        // Только голова: MegaApiClient отдаёт поток, тянущий чанки по мере чтения, — весь архив не качаем.
        await using var s = await client.DownloadAsync(node, progress: null, cancellationToken: ct).ConfigureAwait(false);
        return await ArchiveHead.IsEbkeAsync(s, ct).ConfigureAwait(false);
    }

    /// <summary>Батч: ОДИН логин + уже выгруженный список узлов на весь список (вместо логина на каждый
    /// архив — MEGA рейт-лимитит логины). Голову каждого узла читаем дёшево, тем же клиентом.</summary>
    public async Task<IReadOnlySet<string>> ListEncryptedAsync(IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
            return set;

        var client = await LoginAsync().ConfigureAwait(false);
        var (folder, nodes) = await EnsureFolderAsync(client, create: false).ConfigureAwait(false);
        if (folder is null)
            return set;

        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.Where(n => n.Type == NodeType.File && n.ParentId == folder.Id && wanted.Contains(n.Name)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var s = await client.DownloadAsync(node, progress: null, cancellationToken: ct).ConfigureAwait(false);
                if (await ArchiveHead.IsEbkeAsync(s, ct).ConfigureAwait(false))
                    set.Add(node.Name);
            }
            catch { /* один битый узел не срывает весь список */ }
        }
        return set;
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
}
