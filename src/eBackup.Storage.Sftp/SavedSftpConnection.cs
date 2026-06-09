namespace eBackup.Storage.Sftp;

/// <summary>
/// Сохранённое подключение SFTP в том виде, в каком оно лежит в конфиге.
/// Поля-секреты (<see cref="ProtectedPassword"/>, <see cref="ProtectedKeyPassphrase"/>)
/// хранятся УЖЕ зашифрованными — см. <see cref="SftpConnectionStore"/>.
/// </summary>
public sealed record SavedSftpConnection
{
    /// <summary>Стабильный идентификатор подключения.</summary>
    public required string Id { get; init; }

    /// <summary>Отображаемое имя, напр. «Домашний NAS».</summary>
    public required string Name { get; init; }

    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }

    /// <summary>Зашифрованный пароль (или null, если используется ключ).</summary>
    public string? ProtectedPassword { get; init; }

    /// <summary>Путь к файлу приватного ключа (не секрет сам по себе).</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>Зашифрованная парольная фраза ключа (или null).</summary>
    public string? ProtectedKeyPassphrase { get; init; }

    public string RemoteDirectory { get; init; } = ".";
}
