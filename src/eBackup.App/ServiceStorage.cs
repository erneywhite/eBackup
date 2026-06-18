using eBackup.Ipc.Contracts;
using eBackup.Storage;

namespace eBackup.App;

/// <summary>
/// Помощник для страниц GUI: проекция <see cref="StorageDetail"/> (несекретные поля из службы)
/// в типизированный <see cref="SavedStorage"/> для отображения (список, подписи, DescribeStorage).
/// Секреты сюда не попадают — они остаются в службе под машинным ключом.
/// </summary>
public static class ServiceStorage
{
    public static SavedStorage ToSaved(StorageDetail d)
    {
        var s = d.Settings;
        _ = Enum.TryParse<StorageKind>(d.Kind, ignoreCase: true, out var kind);
        string? Str(string k) => s.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        int Int(string k, int def) => int.TryParse(Str(k), out var v) ? v : def;
        bool Bool(string k, bool def = false) => bool.TryParse(Str(k), out var v) ? v : def;

        return new SavedStorage
        {
            Id = d.Id,
            Name = d.Name,
            Kind = kind,
            Path = Str("path"),
            ShareUsername = Str("shareUsername"),
            Host = Str("host"),
            Port = Int("port", 22),
            Username = Str("username"),
            RemoteDirectory = Str("remoteDirectory"),
            UseFtps = Bool("useFtps"),
            AllowUntrustedCertificate = Bool("allowUntrustedCertificate"),
            ServiceUrl = Str("serviceUrl"),
            Bucket = Str("bucket"),
            AccessKeyId = Str("accessKeyId"),
            ForcePathStyle = Bool("forcePathStyle", def: true),
        };
    }
}
