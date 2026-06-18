namespace eBackup.Core.History;

/// <summary>Запись журнала о запуске бэкапа (страница «История»).</summary>
public sealed class BackupRunRecord
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Вид операции: «бэкап», «восстановление», «извлечение» и т.п.</summary>
    public string Operation { get; set; } = "бэкап";

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Что запустило: «вручную», «расписание „Имя“» и т.п.</summary>
    public string Trigger { get; set; } = "вручную";

    /// <summary>Имя файла архива (null — упали до сборки).</summary>
    public string? ArchiveName { get; set; }

    public List<string> Modules { get; set; } = [];

    public List<string> Targets { get; set; } = [];

    public long SizeBytes { get; set; }

    /// <summary>true/false — итог; null — запуск не завершился (прерван или краш).</summary>
    public bool? Success { get; set; }

    public string? Error { get; set; }

    /// <summary>Сколько файлов пропущено (нет доступа/заняты) — бэкап успешен, но неполный.</summary>
    public int SkippedFiles { get; set; }

    // --- аддитивно для службы 1.2 (старые runs.json без этих полей читаются как null/0) ---

    /// <summary>
    /// Состояние задачи в модели службы: Queued / Running / Completed / CompletedWithErrors /
    /// Failed / Cancelled / Interrupted. null — старая запись (итог берётся из <see cref="Success"/>).
    /// </summary>
    public string? State { get; set; }

    /// <summary>Последний seq журнала этого запуска (для reconnect/replay прогресса по IPC).</summary>
    public long LastSeq { get; set; }

    /// <summary>SID владельца запуска (per-user scoping в службе). null — старая запись.</summary>
    public string? OwnerSid { get; set; }
}
