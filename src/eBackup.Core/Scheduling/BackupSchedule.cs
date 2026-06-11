namespace eBackup.Core.Scheduling;

public enum ScheduleKind
{
    /// <summary>Ежедневно в указанное время.</summary>
    Daily,
    /// <summary>Еженедельно: день недели + время.</summary>
    Weekly,
    /// <summary>Каждые N часов от последнего запуска.</summary>
    EveryHours
}

/// <summary>
/// Расписание автоматического бэкапа. Хранит СВОЙ полный набор настроек
/// (модули/цели/шифрование) — глобальный выключатель модулей на него не влияет.
/// </summary>
public sealed record BackupSchedule
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Id модулей, входящих в этот бэкап (своя выборка расписания).</summary>
    public List<string> ModuleIds { get; init; } = [];

    /// <summary>Включать ли «свои папки» (список — общий, со страницы «Бэкап»).</summary>
    public bool IncludeCustomFolders { get; init; }

    public bool KeepLocal { get; init; } = true;

    /// <summary>Id сохранённых SFTP-подключений — цели заливки.</summary>
    public List<string> TargetConnectionIds { get; init; } = [];

    /// <summary>Парольная фраза шифрования, зашифрованная DPAPI; null — без шифрования.</summary>
    public string? ProtectedPassphrase { get; init; }

    public ScheduleKind Kind { get; init; } = ScheduleKind.Daily;

    /// <summary>Час/минута запуска (для Daily и Weekly).</summary>
    public int Hour { get; init; } = 3;
    public int Minute { get; init; }

    /// <summary>День недели (для Weekly).</summary>
    public DayOfWeek Day { get; init; } = DayOfWeek.Monday;

    /// <summary>Интервал в часах (для EveryHours).</summary>
    public int EveryHours { get; init; } = 6;

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Последний запуск (локальное время). При создании ставится «сейчас», чтобы
    /// расписание не срабатывало немедленно задним числом.
    /// </summary>
    public DateTime? LastRunAt { get; init; }
}
