namespace eBackup.Storage.Sftp;

/// <summary>
/// Параметры подключения к SFTP-серверу. Аутентификация — паролем и/или приватным ключом.
/// (Безопасное хранение этих данных через Windows DPAPI — отдельный шаг 1b.)
/// </summary>
public sealed class SftpConnectionOptions
{
    /// <summary>Хост или IP сервера.</summary>
    public required string Host { get; init; }

    /// <summary>Порт SFTP (по умолчанию 22).</summary>
    public int Port { get; init; } = 22;

    /// <summary>Имя пользователя.</summary>
    public required string Username { get; init; }

    /// <summary>Пароль (способ 1). Можно не задавать, если используется ключ.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// СОДЕРЖИМОЕ приватного ключа (способ 2), PEM/OpenSSH-текст. Намеренно не путь:
    /// ключ хранится зашифрованным в конфиге и не обязан лежать файлом на диске.
    /// </summary>
    public string? PrivateKeyPem { get; init; }

    /// <summary>Парольная фраза приватного ключа, если он зашифрован.</summary>
    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>Удалённая папка, куда складываются архивы (по умолчанию — рабочая папка пользователя).</summary>
    public string RemoteDirectory { get; init; } = ".";
}
