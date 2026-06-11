using eBackup.Core.Security;
using eBackup.Storage.Sftp;

namespace eBackup.Storage;

/// <summary>Собирает рабочее хранилище из записи конфига.</summary>
public static class StorageFactory
{
    public static IArchiveStorage Create(SavedStorage s, ISecretProtector protector) => s.Kind switch
    {
        StorageKind.LocalFolder => new FolderStorage(s, protector),
        StorageKind.Sftp => new SftpArchiveStorage(
            new SftpStorageProvider(ToSftpOptions(s, protector)), s.Name),
        StorageKind.Ftp => new FtpStorage(s, protector),
        _ => throw new NotSupportedException($"Тип хранилища {s.Kind} пока не поддерживается.")
    };

    /// <summary>Параметры SFTP из записи (секреты расшифровываются).</summary>
    public static SftpConnectionOptions ToSftpOptions(SavedStorage s, ISecretProtector protector) => new()
    {
        Host = s.Host ?? throw new InvalidOperationException("У SFTP-хранилища не задан хост."),
        Port = s.Port,
        Username = s.Username ?? throw new InvalidOperationException("У SFTP-хранилища не задан логин."),
        Password = s.ProtectedPassword is null ? null : protector.Unprotect(s.ProtectedPassword),
        PrivateKeyPem = s.ProtectedPrivateKey is null ? null : protector.Unprotect(s.ProtectedPrivateKey),
        PrivateKeyPassphrase = s.ProtectedKeyPassphrase is null ? null : protector.Unprotect(s.ProtectedKeyPassphrase),
        RemoteDirectory = string.IsNullOrWhiteSpace(s.RemoteDirectory) ? "." : s.RemoteDirectory
    };
}
