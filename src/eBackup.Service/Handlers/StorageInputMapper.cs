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
    /// <summary>
    /// DTO → SavedStorage. Открытые секреты шифруются машинным ключом; если секрет в DTO не передан
    /// (поле оставлено пустым в редакторе), берём прежнее зашифрованное значение из <paramref name="existing"/>
    /// — так «оставить прежний пароль» работает и через границу IPC, не теряя секрет.
    /// </summary>
    public static SavedStorage ToSavedStorage(StorageInput i, StorageStore protector, SavedStorage? existing = null)
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
            ProtectedSharePassword = Enc(protector, sec, "sharePassword", existing?.ProtectedSharePassword),
            ProtectedPassword = Enc(protector, sec, "password", existing?.ProtectedPassword),
            ProtectedPrivateKey = Enc(protector, sec, "privateKey", existing?.ProtectedPrivateKey),
            ProtectedKeyPassphrase = Enc(protector, sec, "keyPassphrase", existing?.ProtectedKeyPassphrase),
            ProtectedSecretKey = Enc(protector, sec, "secretKey", existing?.ProtectedSecretKey),
            ProtectedOAuthToken = Enc(protector, sec, "oauthToken", existing?.ProtectedOAuthToken),
        };
    }

    /// <summary>SavedStorage → подробности для редактора: НЕсекретные поля + имена присутствующих секретов
    /// (открытые секреты НИКОГДА не покидают службу — редактор лишь показывает «оставить прежний»).</summary>
    public static StorageDetail ToDetail(SavedStorage s)
    {
        var settings = new Dictionary<string, string>();
        void Put(string k, string? v) { if (!string.IsNullOrEmpty(v)) settings[k] = v!; }
        Put("path", s.Path);
        Put("shareUsername", s.ShareUsername);
        Put("host", s.Host);
        settings["port"] = s.Port.ToString();
        Put("username", s.Username);
        Put("remoteDirectory", s.RemoteDirectory);
        settings["useFtps"] = s.UseFtps.ToString();
        settings["allowUntrustedCertificate"] = s.AllowUntrustedCertificate.ToString();
        Put("serviceUrl", s.ServiceUrl);
        Put("bucket", s.Bucket);
        Put("accessKeyId", s.AccessKeyId);
        settings["forcePathStyle"] = s.ForcePathStyle.ToString();

        var secrets = new List<string>();
        void Has(string k, string? v) { if (v is not null) secrets.Add(k); }
        Has("sharePassword", s.ProtectedSharePassword);
        Has("password", s.ProtectedPassword);
        Has("privateKey", s.ProtectedPrivateKey);
        Has("keyPassphrase", s.ProtectedKeyPassphrase);
        Has("secretKey", s.ProtectedSecretKey);
        Has("oauthToken", s.ProtectedOAuthToken);

        return new StorageDetail
        {
            Id = s.Id,
            Name = s.Name,
            Kind = s.Kind.ToString(),
            Settings = settings,
            PresentSecrets = secrets.ToArray(),
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

    private static string? Enc(StorageStore protector, Dictionary<string, string>? secrets, string k, string? existing)
    {
        var v = Str(secrets, k);
        return v is null ? existing : protector.Protect(v); // нет открытого секрета → оставляем прежний (машинный ключ)
    }
}
