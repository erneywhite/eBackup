using System.Text.Json;
using eBackup.Core.Security;

namespace eBackup.Storage.Sftp;

/// <summary>
/// Хранилище сохранённых SFTP-подключений (JSON-файл). Секреты шифруются/расшифровываются
/// через переданный <see cref="ISecretProtector"/> (по умолчанию — Windows DPAPI),
/// поэтому в файле паролей в открытом виде нет.
/// По умолчанию файл лежит в %LOCALAPPDATA%\eBackup\connections.json (не роумится между ПК,
/// что согласуется с привязкой DPAPI к машине/пользователю).
/// </summary>
public sealed class SftpConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ISecretProtector _protector;
    private readonly string _filePath;

    public SftpConnectionStore(ISecretProtector protector, string? filePath = null)
    {
        _protector = protector;
        _filePath = filePath ?? DefaultFilePath();
    }

    /// <summary>Путь к файлу подключений по умолчанию.</summary>
    public static string DefaultFilePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "eBackup", "connections.json");

    /// <summary>Прочитать все сохранённые подключения (секреты остаются зашифрованными).</summary>
    public async Task<IReadOnlyList<SavedSftpConnection>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return [];

        await using var stream = File.OpenRead(_filePath);
        var list = await JsonSerializer
            .DeserializeAsync<List<SavedSftpConnection>>(stream, JsonOptions, ct)
            .ConfigureAwait(false);
        return list ?? [];
    }

    /// <summary>
    /// Перезаписать весь список подключений. Запись атомарная (temp-файл + переименование),
    /// чтобы обрыв процесса на середине не оставил повреждённый конфиг.
    /// </summary>
    public async Task SaveAllAsync(IEnumerable<SavedSftpConnection> connections, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
        {
            await JsonSerializer
                .SerializeAsync(stream, connections.ToList(), JsonOptions, ct)
                .ConfigureAwait(false);
        }
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    /// <summary>Создать сохраняемую запись из «сырых» параметров — секреты шифруются.</summary>
    public SavedSftpConnection Protect(string id, string name, SftpConnectionOptions options)
        => new()
        {
            Id = id,
            Name = name,
            Host = options.Host,
            Port = options.Port,
            Username = options.Username,
            ProtectedPassword = options.Password is null ? null : _protector.Protect(options.Password),
            ProtectedPrivateKey = options.PrivateKeyPem is null ? null : _protector.Protect(options.PrivateKeyPem),
            ProtectedKeyPassphrase = options.PrivateKeyPassphrase is null ? null : _protector.Protect(options.PrivateKeyPassphrase),
            RemoteDirectory = options.RemoteDirectory
        };

    /// <summary>Развернуть сохранённую запись в рабочие параметры — секреты расшифровываются.</summary>
    public SftpConnectionOptions Unprotect(SavedSftpConnection c)
        => new()
        {
            Host = c.Host,
            Port = c.Port,
            Username = c.Username,
            Password = c.ProtectedPassword is null ? null : _protector.Unprotect(c.ProtectedPassword),
            PrivateKeyPem = c.ProtectedPrivateKey is null ? null : _protector.Unprotect(c.ProtectedPrivateKey),
            PrivateKeyPassphrase = c.ProtectedKeyPassphrase is null ? null : _protector.Unprotect(c.ProtectedKeyPassphrase),
            RemoteDirectory = c.RemoteDirectory
        };
}
