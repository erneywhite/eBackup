namespace eBackup.Core.Scheduling;

public enum ScheduleKind
{
    /// <summary>Ежедневно в указанное время (для постоянно работающих машин).</summary>
    Daily,
    /// <summary>Еженедельно: день недели + время.</summary>
    Weekly,
    /// <summary>Каждые N часов от последнего запуска.</summary>
    EveryHours,
    /// <summary>Раз в день, когда ПК в простое — момент выбирает приложение.</summary>
    DailyWhenIdle
}

/// <summary>
/// Расписание автоматического бэкапа. Хранит СВОЙ полный набор настроек
/// (модули/цели/шифрование) — глобальный выключатель модулей на него не влияет.
/// </summary>
public sealed record BackupSchedule
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>SID владельца (кто создал) — служба резолвит источники per-SID и проверяет права.</summary>
    public string? OwnerSid { get; init; }

    /// <summary>Id модулей, входящих в этот бэкап (своя выборка расписания).</summary>
    public List<string> ModuleIds { get; init; } = [];

    /// <summary>Свои папки ЭТОГО расписания (не зависят от списка на странице «Бэкап»).</summary>
    public List<string> CustomFolders { get; init; } = [];

    public bool KeepLocal { get; init; } = true;

    /// <summary>Id сохранённых SFTP-подключений — цели заливки.</summary>
    public List<string> TargetConnectionIds { get; init; } = [];

    /// <summary>Парольная фраза шифрования, зашифрованная DPAPI; null — без шифрования.</summary>
    public string? ProtectedPassphrase { get; init; }

    public ScheduleKind Kind { get; init; } = ScheduleKind.Daily;

    /// <summary>Час/минута запуска (для Daily и Weekly).</summary>
    public int Hour { get; init; } = 3;
    public int Minute { get; init; }

    /// <summary>Дни недели (для Weekly) — можно несколько.</summary>
    public List<DayOfWeek> Days { get; init; } = [DayOfWeek.Monday];

    /// <summary>Интервал в часах (для EveryHours).</summary>
    public int EveryHours { get; init; } = 6;

    /// <summary>Сколько минут без ввода считать простоем (для DailyWhenIdle).</summary>
    public int IdleMinutes { get; init; } = 5;

    public bool Enabled { get; init; } = true;

    // Настройки прогона: у службы нет per-user AppSettings, поэтому расписание несёт их само
    // (раньше брались из настроек GUI). 0 быстрое / 1 обычное / 2 максимальное сжатие.
    public int CompressionMode { get; init; }
    public bool IncludeMachineName { get; init; }
    public int? RetentionCount { get; init; }

    /// <summary>
    /// Последний запуск (локальное время). При создании ставится «сейчас», чтобы
    /// расписание не срабатывало немедленно задним числом.
    /// </summary>
    public DateTime? LastRunAt { get; init; }
}
