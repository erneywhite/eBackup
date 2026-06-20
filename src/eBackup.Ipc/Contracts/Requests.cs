namespace eBackup.Ipc.Contracts;

// Запросы IPC. Везде — ИДЕНТИФИКАТОРЫ и опции, НИКОГДА не богатые объекты или секреты.
// Имена операций (Op во фрейме) — см. IpcOps.

/// <summary>Имена операций (строки во фрейме).</summary>
public static class IpcOps
{
    public const string Hello = "hello";
    public const string StartBackup = "startBackup";
    public const string StartRestore = "startRestore";
    public const string RunScheduleNow = "runScheduleNow";
    public const string CancelJob = "cancelJob";
    public const string GetJob = "getJob";
    public const string ListJobs = "listJobs";
    public const string AttachToJob = "attachToJob";
    public const string DetachFromJob = "detachFromJob";
    public const string StashPassphrase = "stashPassphrase";
    public const string ListStorages = "listStorages";
    public const string ListStorageDetails = "listStorageDetails";
    public const string GetStorage = "getStorage";
    public const string UpsertStorage = "upsertStorage";
    public const string DeleteStorage = "deleteStorage";
    public const string TestStorage = "testStorage";
    public const string ListArchives = "listArchives";
    public const string OpenArchiveRead = "openArchiveRead";
    public const string ReadArchiveChunk = "readArchiveChunk";
    public const string CloseArchiveRead = "closeArchiveRead";
    public const string DeleteArchive = "deleteArchive";
    public const string ListSchedules = "listSchedules";
    public const string UpsertSchedule = "upsertSchedule";
    public const string DeleteSchedule = "deleteSchedule";
    public const string ListModules = "listModules";
    public const string SetModuleEnabled = "setModuleEnabled";
    public const string InstallModule = "installModule";
    public const string DeleteModule = "deleteModule";
    public const string DiscoverModule = "discoverModule";
    public const string ListCustomFolders = "listCustomFolders";
    public const string UpsertCustomFolder = "upsertCustomFolder";
    public const string DeleteCustomFolder = "deleteCustomFolder";
    public const string ListHistory = "listHistory";
    public const string GetRunLog = "getRunLog";
}

public sealed record HelloRequest
{
    public int ClientProtocol { get; init; } = IpcContractInfo.WireProtocol;
    public string ClientBuild { get; init; } = "";

    /// <summary>Язык UI GUI ("ru"/"en") — служба применяет его к своим строкам (фазы/журнал/ошибки)
    /// и запоминает для фоновых/плановых прогонов. null — не менять.</summary>
    public string? UiLanguage { get; init; }
}

public sealed record StartBackupRequest
{
    public string[] ModuleIds { get; init; } = [];
    public string[] CustomFolderIds { get; init; } = []; // по id зарегистрированных папок, НЕ сырые пути
    public string[] TargetStorageIds { get; init; } = [];
    public int CompressionMode { get; init; }
    public PassphraseRef? Passphrase { get; init; }
    public bool IncludeMachineName { get; init; }
    public int? RetentionCount { get; init; }
    public string Trigger { get; init; } = "";
    public string ClientRequestId { get; init; } = ""; // GUID клиента → идемпотентность при потере связи
}

/// <summary>Восстановление в службе: источник (хранилище+архив), цель и режим. Для зашифрованного
/// архива — <see cref="Passphrase"/> через разовый тикет (как у бэкапа); null/none — незашифрованный.</summary>
public sealed record StartRestoreRequest
{
    public string SourceStorageId { get; init; } = "";
    public string RemoteName { get; init; } = "";
    public string? TargetDir { get; init; }                 // null — в исходные места
    public string Policy { get; init; } = "BackupExisting";  // ConflictPolicy строкой
    public string? AssetsDir { get; init; }
    public PassphraseRef? Passphrase { get; init; }          // тикет фразы для EBKE; null — без расшифровки
    public string Trigger { get; init; } = "";
    public string ClientRequestId { get; init; } = "";
}

public sealed record RunScheduleNowRequest
{
    public string ScheduleId { get; init; } = "";
    public string ClientRequestId { get; init; } = "";
}

public sealed record CancelJobRequest { public string JobId { get; init; } = ""; }
public sealed record GetJobRequest { public string JobId { get; init; } = ""; }
public sealed record ListJobsRequest { public bool IncludeFinished { get; init; } public int Limit { get; init; } = 50; }
public sealed record AttachRequest { public string JobId { get; init; } = ""; public long FromSeq { get; init; } }
public sealed record DetachRequest { public string JobId { get; init; } = ""; }

/// <summary>Разовая ad-hoc фраза. Plaintext пересекает пайп один раз, после проверки server==SYSTEM.</summary>
public sealed record StashPassphraseRequest { public string Plaintext { get; init; } = ""; }

public sealed record DeleteByIdRequest { public string Id { get; init; } = ""; }

public sealed record GetStorageRequest { public string Id { get; init; } = ""; }

public sealed record ListArchivesRequest
{
    public string StorageId { get; init; } = "";
    /// <summary>Подглядывать ли шифрование (метка 🔒). Дорого для облаков — «Обзор» (только счётчик) не просит.</summary>
    public bool IncludeEncryption { get; init; }
}

// --- seek-чтение архива в хранилище службы (браузер/восстановление без полной закачки) ---
public sealed record OpenArchiveReadRequest { public string StorageId { get; init; } = ""; public string RemoteName { get; init; } = ""; }
public sealed record ReadArchiveChunkRequest { public string Handle { get; init; } = ""; public long Offset { get; init; } public int Count { get; init; } }
public sealed record CloseArchiveReadRequest { public string Handle { get; init; } = ""; }
public sealed record DeleteArchiveRequest { public string StorageId { get; init; } = ""; public string RemoteName { get; init; } = ""; }

public sealed record StorageInput
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public Dictionary<string, string>? Settings { get; init; }
    /// <summary>Открытые секреты по полю (служба перешифрует машинным ключом и выбросит).</summary>
    public Dictionary<string, string>? PlaintextSecrets { get; init; }
}

public sealed record TestStorageRequest
{
    public string? StorageId { get; init; }   // тест сохранённого…
    public StorageInput? Inline { get; init; } // …или ещё не сохранённого
}

public sealed record ScheduleInput
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string[] ModuleIds { get; init; } = [];
    public string[] CustomFolderIds { get; init; } = [];
    public string[] TargetStorageIds { get; init; } = [];
    public string Kind { get; init; } = "Daily"; // enum строкой
    public int Hour { get; init; } = 3;
    public int Minute { get; init; }
    public int[] Days { get; init; } = [];
    public int EveryHours { get; init; } = 6;
    public int IdleMinutes { get; init; } = 5;
    public bool Enabled { get; init; } = true;
    public int CompressionMode { get; init; }
    public bool IncludeMachineName { get; init; }
    public int? RetentionCount { get; init; }
    // Парольная фраза задаётся как открытый текст один раз (служба шифрует машинным ключом):
    // null — оставить прежнюю; "" — снять шифрование; иначе — задать. Как PlaintextSecrets у хранилищ.
    public string? NewPassphrase { get; init; }
}

public sealed record SetModuleEnabledRequest { public string Id { get; init; } = ""; public bool Enabled { get; init; } }
public sealed record InstallModuleRequest { public string? CatalogRef { get; init; } public string? DeclarativeJson { get; init; } }
public sealed record DiscoverModuleRequest { public string Id { get; init; } = ""; }

/// <summary>Зарегистрировать «свою папку» (id = путь). Удаление — через DeleteByIdRequest (Id = путь).</summary>
public sealed record UpsertCustomFolderRequest { public string Path { get; init; } = ""; }

public sealed record ListHistoryRequest { public int Limit { get; init; } = 100; }
public sealed record GetRunLogRequest { public string RunId { get; init; } = ""; public long FromSeq { get; init; } public int MaxLines { get; init; } = 500; }
