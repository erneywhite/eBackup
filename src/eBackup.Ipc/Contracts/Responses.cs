namespace eBackup.Ipc.Contracts;

// Ответы IPC. Метаданные/сводки — НИКОГДА зашифрованные секреты.

public sealed record HelloResponse
{
    public int ServerProtocol { get; init; } = IpcContractInfo.WireProtocol;
    public int MinClientProtocol { get; init; } = IpcContractInfo.MinClientProtocol;
    public string ServerBuild { get; init; } = "";
    /// <summary>Меняется на каждый запуск процесса службы → GUI узнаёт рестарт.</summary>
    public string ServiceInstanceId { get; init; } = "";
    public string[] Capabilities { get; init; } = [];
    /// <summary>Удалось ли службе сопоставить вызывающего с профилем (per-OwnerSid резолв).</summary>
    public bool UserResolved { get; init; }
}

public sealed record StartBackupResponse
{
    public string JobId { get; init; } = "";
    public string RunId { get; init; } = "";
    public int Position { get; init; } // место в очереди (0 = выполняется)
}

public sealed record JobStatus
{
    public string JobId { get; init; } = "";
    public string RunId { get; init; } = "";
    public string State { get; init; } = "";   // Queued/Running/Completed/CompletedWithErrors/Failed/Cancelled/Interrupted
    public string Phase { get; init; } = "";
    public double ProgressFraction { get; init; }
    public string Trigger { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? ArchiveName { get; init; }
    public long SizeBytes { get; init; }
    public int SkippedFiles { get; init; }
    public string OwnerSid { get; init; } = "";
    public string JobOrigin { get; init; } = "Interactive"; // Interactive | Scheduled
    public bool IsCancelling { get; init; }
    public string[] Modules { get; init; } = [];
    public string[] Targets { get; init; } = [];
    public string[] TargetsDone { get; init; } = [];
    public string[] TargetsFailed { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record AttachAck
{
    public bool Accepted { get; init; }
    public long LowestRetainedSeq { get; init; }
    public long NextSeq { get; init; }
    public bool Finished { get; init; }
}

public sealed record StashPassphraseResponse
{
    public string Ticket { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record StorageSummary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public bool HasSecret { get; init; }
}

/// <summary>Файл-архив в хранилище (листинг через службу — у GUI нет секрета хранилища).</summary>
public sealed record RemoteFileDto
{
    public string Name { get; init; } = "";
    public long Length { get; init; }
    public DateTime LastWriteTime { get; init; }
}

/// <summary>Полные НЕсекретные поля хранилища для редактора + имена присутствующих секретов
/// (открытые секреты не передаются — редактор показывает «оставить прежний»).</summary>
public sealed record StorageDetail
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public Dictionary<string, string> Settings { get; init; } = [];
    public string[] PresentSecrets { get; init; } = [];
}

public sealed record ScheduleSummary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public bool Enabled { get; init; }
    public bool HasPassphrase { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
}

public sealed record ModuleSummary
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Source { get; init; } = ""; // BuiltIn | Declarative
    public bool Enabled { get; init; }
    public string? Problem { get; init; }
}

public sealed record BackupRunRecordDto
{
    public string Id { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string Trigger { get; init; } = "";
    public bool? Success { get; init; }
    public string? State { get; init; }
    public long SizeBytes { get; init; }
    public int SkippedFiles { get; init; }
    public string[] Modules { get; init; } = [];
    public string[] Targets { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record TestResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public long? FreeBytes { get; init; }
}

public sealed record LogLine { public long Seq { get; init; } public string Text { get; init; } = ""; }

public sealed record GetRunLogResponse
{
    public LogLine[] Lines { get; init; } = [];
    public long NextSeq { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>Универсальный «ок» для команд без полезной нагрузки.</summary>
public sealed record Ack { public bool Ok { get; init; } = true; }
