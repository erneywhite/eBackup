using System.Text.Json;
using eBackup.Core.Model;
using eBackup.Core.Security;
using eBackup.Storage.Sftp;

namespace eBackup.Storage;

/// <summary>
/// Единый конфиг хранилищ (%LOCALAPPDATA%/eBackup/storages.json). Секреты — DPAPI.
/// При первом запуске автоматически мигрирует старый connections.json (SFTP):
/// id сохраняются, поэтому ссылки из расписаний не ломаются.
/// </summary>
public sealed class StorageStore(
    ISecretProtector protector,
    string? filePath = null,
    string? legacyConnectionsPath = null)
{
    private readonly string _filePath = filePath ?? DefaultFilePath();
    private readonly string _legacyPath = legacyConnectionsPath ?? SftpConnectionStore.DefaultFilePath();

    public ISecretProtector Protector => protector;

    public static string DefaultFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "eBackup", "storages.json");

    public async Task<IReadOnlyList<SavedStorage>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            var migrated = await TryMigrateLegacyAsync(ct).ConfigureAwait(false);
            if (migrated is not null)
                return migrated;
            return [];
        }

        await using var stream = File.OpenRead(_filePath);
        var list = await JsonSerializer
            .DeserializeAsync<List<SavedStorage>>(stream, ManifestJson.Options, ct)
            .ConfigureAwait(false);
        return list ?? [];
    }

    public async Task SaveAllAsync(IEnumerable<SavedStorage> storages, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = _filePath + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer
                .SerializeAsync(stream, storages.ToList(), ManifestJson.Options, ct)
                .ConfigureAwait(false);
        }
        File.Move(tmp, _filePath, overwrite: true);
    }

    public string Protect(string secret) => protector.Protect(secret);
    public string Unprotect(string protectedValue) => protector.Unprotect(protectedValue);

    /// <summary>Старый конфиг SFTP-подключений → единый список хранилищ.</summary>
    private async Task<IReadOnlyList<SavedStorage>?> TryMigrateLegacyAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_legacyPath))
                return null;

            List<SavedSftpConnection>? legacy;
            await using (var stream = File.OpenRead(_legacyPath))
            {
                legacy = await JsonSerializer
                    .DeserializeAsync<List<SavedSftpConnection>>(stream, ManifestJson.Options, ct)
                    .ConfigureAwait(false);
            }
            if (legacy is null || legacy.Count == 0)
                return null;

            // Protected-поля переносим как есть: это те же DPAPI-блобы того же пользователя.
            var storages = legacy.Select(c => new SavedStorage
            {
                Id = c.Id,
                Name = c.Name,
                Kind = StorageKind.Sftp,
                Host = c.Host,
                Port = c.Port,
                Username = c.Username,
                ProtectedPassword = c.ProtectedPassword,
                ProtectedPrivateKey = c.ProtectedPrivateKey,
                ProtectedKeyPassphrase = c.ProtectedKeyPassphrase,
                RemoteDirectory = c.RemoteDirectory
            }).ToList();

            await SaveAllAsync(storages, ct).ConfigureAwait(false);
            return storages;
        }
        catch
        {
            return null; // битый старый конфиг — начнём с пустого списка, не падаем
        }
    }
}
