using eBackup.Ipc.Contracts;
using eBackup.Storage;

namespace eBackup.Service.Handlers;

/// <summary>
/// Преобразование между IPC-DTO хранилища (плоские словари настроек/секретов) и типизированной
/// <see cref="SavedStorage"/>. Открытые секреты сразу шифруются машинным ключом через переданный
/// <see cref="StorageStore"/> (служба под SYSTEM) и в SavedStorage попадают только Protected*-поля.
/// </summary>
public static class StorageInputMapper
{
    public static SavedStorage ToSavedStorage(StorageInput i, StorageStore protector)
    {
        var s = i.Settings;
        var sec = i.PlaintextSecrets;
        _ = Enum.TryParse<StorageKind>(i.Kind, ignoreCase: true, out var kind);

        return new SavedStorage
        {
            Id = i.Id,
            Name = i.Name,
            Kind = kind,
            Path = Str(s, "path"),
            ShareUsername = Str(s, "shareUsername"),
            Host = Str(s, "host"),
            Port = Int(s, "port", 22),
            Username = Str(s, "username"),
            RemoteDirectory = Str(s, "remoteDirectory"),
            UseFtps = Bool(s, "useFtps"),
            AllowUntrustedCertificate = Bool(s, "allowUntrustedCertificate"),
            ServiceUrl = Str(s, "serviceUrl"),
            Bucket = Str(s, "bucket"),
            AccessKeyId = Str(s, "accessKeyId"),
            ForcePathStyle = Bool(s, "forcePathStyle", def: true),
            ProtectedSharePassword = Enc(protector, sec, "sharePassword"),
            ProtectedPassword = Enc(protector, sec, "password"),
            ProtectedPrivateKey = Enc(protector, sec, "privateKey"),
            ProtectedKeyPassphrase = Enc(protector, sec, "keyPassphrase"),
            ProtectedSecretKey = Enc(protector, sec, "secretKey"),
            ProtectedOAuthToken = Enc(protector, sec, "oauthToken"),
        };
    }

    public static StorageSummary ToSummary(SavedStorage s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Kind = s.Kind.ToString(),
        HasSecret = s.ProtectedSharePassword is not null || s.ProtectedPassword is not null
            || s.ProtectedPrivateKey is not null || s.ProtectedKeyPassphrase is not null
            || s.ProtectedSecretKey is not null || s.ProtectedOAuthToken is not null,
    };

    private static string? Str(Dictionary<string, string>? d, string k)
        => d is not null && d.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    private static int Int(Dictionary<string, string>? d, string k, int def)
        => int.TryParse(Str(d, k), out var v) ? v : def;

    private static bool Bool(Dictionary<string, string>? d, string k, bool def = false)
        => bool.TryParse(Str(d, k), out var v) ? v : def;

    private static string? Enc(StorageStore protector, Dictionary<string, string>? secrets, string k)
    {
        var v = Str(secrets, k);
        return v is null ? null : protector.Protect(v); // машинный ключ
    }
}
