namespace eBackup.Core.Model;

/// <summary>Тип записи в манифесте.</summary>
public enum PathEntryType
{
    File,
    Directory,
    RegistryKey
}

/// <summary>
/// Одна запись бэкапа: токенизированный исходный путь, его тип и относительный путь
/// внутри архива. Токенизация (см. <c>PathTokens</c>) делает архив переносимым между
/// машинами и переживает переустановку ОС.
/// </summary>
public sealed record PathEntry
{
    /// <summary>Исходный путь в токенизированном виде, напр. "{APPDATA}/obs-studio".</summary>
    public required string TokenPath { get; init; }

    /// <summary>Тип записи.</summary>
    public PathEntryType Type { get; init; } = PathEntryType.File;

    /// <summary>Относительный путь внутри архива (в каталоге data/), напр. "obs/obs-studio".</summary>
    public required string ArchivePath { get; init; }

    /// <summary>SHA-256 содержимого (для файлов) — для верификации при восстановлении.</summary>
    public string? Sha256 { get; init; }

    /// <summary>
    /// Для записей-каталогов: glob-маски (через «/») того, что НЕ включать в бэкап,
    /// напр. "logs/**". Обобщённый механизм — конкретные маски задаёт модуль.
    /// </summary>
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = [];

    /// <summary>
    /// true — запись упаковывается движком на бэкапе, но НЕ восстанавливается им
    /// автоматически: её размещением занимается restore-хук модуля (например, ассеты
    /// OBS — их кладут в отдельную папку и переписывают пути в сценах).
    /// </summary>
    public bool ManagedByModule { get; init; }
}

/// <summary>Записи одного модуля внутри манифеста.</summary>
public sealed record ModuleEntry
{
    public required string ModuleId { get; init; }
    public required string DisplayName { get; init; }
    public List<PathEntry> Entries { get; init; } = new();
}

/// <summary>Сведения о машине-источнике — для подсказок при восстановлении на другой ПК.</summary>
public sealed record SourceMachine
{
    public string? MachineName { get; init; }
    public string? UserName { get; init; }
    public string? OsVersion { get; init; }
}

/// <summary>
/// Манифест архива eBackup. Кладётся в корень .ebk и описывает, что и откуда снято,
/// чтобы при распаковке (в т.ч. на другом ПК / после переустановки ОС) разложить
/// файлы по местам.
/// </summary>
public sealed record Manifest
{
    /// <summary>Версия формата манифеста.</summary>
    public string FormatVersion { get; init; } = "1.0";

    /// <summary>Когда создан бэкап (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Источник.</summary>
    public SourceMachine? Source { get; init; }

    /// <summary>Модули, вошедшие в архив.</summary>
    public List<ModuleEntry> Modules { get; init; } = new();
}
