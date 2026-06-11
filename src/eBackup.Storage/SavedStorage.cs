namespace eBackup.Storage;

/// <summary>Тип хранилища. Словарь будет расти: Ftp, S3, GoogleDrive, Dropbox…</summary>
public enum StorageKind
{
    /// <summary>Локальная папка, прокинутый сетевой диск или UNC-путь (опц. с авторизацией SMB).</summary>
    LocalFolder,
    Sftp,
    /// <summary>FTP / FTPS (явный TLS).</summary>
    Ftp,
    /// <summary>S3-совместимое: AWS S3, MinIO, Backblaze B2, Cloudflare R2 и т.п.</summary>
    S3,
    /// <summary>WebDAV: Nextcloud, ownCloud, Яндекс.Диск и т.п. (базовый URL + логин/пароль).</summary>
    WebDav,
    /// <summary>Google Drive (OAuth, scope drive.file — видит только свои файлы).</summary>
    GoogleDrive,
    /// <summary>Dropbox (OAuth, изолированная папка приложения Apps/…).</summary>
    Dropbox
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
    /// <summary>FTPS (явный TLS). По умолчанию сертификат проверяется по системным корням.</summary>
    public bool UseFtps { get; init; }

    /// <summary>Принимать непроверенный/самоподписанный TLS-сертификат FTPS (НЕБЕЗОПАСНО:
    /// открывает MITM). Только явный выбор пользователя для NAS с самоподписанным сертификатом.</summary>
    public bool AllowUntrustedCertificate { get; init; }

    // ---- S3 ----
    /// <summary>Endpoint: https://s3.eu-central-1.amazonaws.com, https://…r2.cloudflarestorage.com, http://minio:9000…</summary>
    public string? ServiceUrl { get; init; }

    public string? Bucket { get; init; }

    /// <summary>Access Key ID (не секрет; секретный ключ — в ProtectedSecretKey).</summary>
    public string? AccessKeyId { get; init; }

    /// <summary>Secret Access Key, зашифрован DPAPI.</summary>
    public string? ProtectedSecretKey { get; init; }

    /// <summary>Path-style адресация (нужна MinIO и большинству S3-совместимых; для AWS можно выключить).</summary>
    public bool ForcePathStyle { get; init; } = true;

    // ---- OAuth-облака (Google Drive, Dropbox) ----
    /// <summary>Refresh-токен пользователя, зашифрован DPAPI.</summary>
    public string? ProtectedOAuthToken { get; init; }
}
