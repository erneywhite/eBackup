namespace eBackup.Storage;

/// <summary>Тип хранилища. Словарь будет расти: Ftp, S3, GoogleDrive, Dropbox…</summary>
public enum StorageKind
{
    /// <summary>Локальная папка, прокинутый сетевой диск или UNC-путь (опц. с авторизацией SMB).</summary>
    LocalFolder,
    Sftp,
    /// <summary>FTP / FTPS (явный TLS).</summary>
    Ftp
}

/// <summary>
/// Хранилище в едином конфиге (storages.json). Поля по типам — плоско и nullable:
/// просто для JSON и расширения новыми типами. Секреты — только в Protected*-полях (DPAPI).
/// </summary>
public sealed record SavedStorage
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required StorageKind Kind { get; init; }

    // ---- LocalFolder ----
    /// <summary>Путь: «D:\Backups», «Z:\…» или «\\nas\share\…».</summary>
    public string? Path { get; init; }

    /// <summary>Логин для сетевой папки (опц.; подключение временное, в системе не сохраняется).</summary>
    public string? ShareUsername { get; init; }

    /// <summary>Пароль сетевой папки, зашифрован DPAPI (опц.).</summary>
    public string? ProtectedSharePassword { get; init; }

    // ---- Sftp / Ftp (общие поля подключения) ----
    public string? Host { get; init; }
    public int Port { get; init; } = 22;
    public string? Username { get; init; }
    public string? ProtectedPassword { get; init; }
    public string? ProtectedPrivateKey { get; init; }
    public string? ProtectedKeyPassphrase { get; init; }
    public string? RemoteDirectory { get; init; }

    // ---- Ftp ----
    /// <summary>FTPS (явный TLS). Сертификат принимается любой — ради NAS с самоподписанными.</summary>
    public bool UseFtps { get; init; }
}
