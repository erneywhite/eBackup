namespace eBackup.Ipc.Contracts;

/// <summary>
/// Ошибка по запросу. Через границу привилегий идут ТОЛЬКО типизированные коды + безопасный
/// для показа текст — НИКОГДА сырой текст исключения или сериализованные CLR-типы.
/// </summary>
public sealed record IpcError
{
    public string Code { get; init; } = IpcErrorCodes.Internal;
    public string Message { get; init; } = "";
    public bool Retryable { get; init; }
    public Dictionary<string, string>? Details { get; init; }
}

/// <summary>Закрытый append-only список кодов ошибок IPC.</summary>
public static class IpcErrorCodes
{
    public const string VersionMismatch = "VersionMismatch";
    public const string NotAuthorized = "NotAuthorized";
    public const string NotOwner = "NotOwner";
    public const string BadRequest = "BadRequest";
    public const string NotFound = "NotFound";
    public const string Conflict = "Conflict";
    public const string Busy = "Busy";
    public const string StorageUnavailable = "StorageUnavailable";
    public const string ModuleError = "ModuleError";
    public const string SecretsNotMigrated = "SecretsNotMigrated";   // легаси DPAPI-секрет, служба не читает
    public const string SecretUnavailable = "SecretUnavailable";     // машинный ключ отсутствует/сменился
    public const string PassphraseTicketExpired = "PassphraseTicketExpired";
    public const string Cancelled = "Cancelled";
    public const string RateLimited = "RateLimited";
    public const string Oversized = "Oversized";
    public const string Unsupported = "Unsupported";                 // неизвестная операция / нет capability
    public const string Internal = "Internal";
}
