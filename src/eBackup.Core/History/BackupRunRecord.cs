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
}
