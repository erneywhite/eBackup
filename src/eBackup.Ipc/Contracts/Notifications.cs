namespace eBackup.Ipc.Contracts;

/// <summary>Типы job-нотификаций (Op во фрейме note).</summary>
public static class IpcNotes
{
    public const string Phase = "phase";           // крупная фаза (IProgress<string>) + доля прогресса
    public const string Log = "log";               // детальная строка (Action<string>) — эвиктится при переполнении
    public const string State = "state";           // смена состояния задачи
    public const string LogTruncated = "logTruncated"; // часть лога выпала из буфера — добрать через GetRunLog
    public const string Resync = "resync";
    public const string Completed = "completed";    // терминальная — несёт финальный JobStatus
}

public sealed record PhaseNote
{
    public string JobId { get; init; } = "";
    public long Seq { get; init; }
    public string Phase { get; init; } = "";
    public double ProgressFraction { get; init; }
}

public sealed record LogNote
{
    public string JobId { get; init; } = "";
    public long Seq { get; init; }
    public string Text { get; init; } = "";
}

public sealed record StateChangedNote
{
    public string JobId { get; init; } = "";
    public long Seq { get; init; }
    public string State { get; init; } = "";
}

public sealed record LogTruncatedNote
{
    public string JobId { get; init; } = "";
    public long Seq { get; init; }
    public long FromSeq { get; init; }
    public long ToSeq { get; init; }
}

public sealed record ResyncNote
{
    public string JobId { get; init; } = "";
    public long Seq { get; init; }
    public long NextSeq { get; init; }
}

public sealed record CompletedNote
{
    public string JobId { get; init; } = "";
    public long Seq { get; init; }
    public JobStatus Status { get; init; } = new();
}
