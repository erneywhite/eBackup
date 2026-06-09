using eBackup.Core.Model;

namespace eBackup.Core.Abstractions;

/// <summary>
/// Контекст для restore-хука модуля. Доступ к данным архива заужен ТОЛЬКО до записей
/// этого модуля (под data/&lt;id&gt;/), чтобы недоверенный код-модуль не видел чужие модули,
/// манифест и не мог управлять общим архивом.
/// </summary>
public sealed class ModuleRestoreContext
{
    /// <summary>
    /// Открывает поток записи этого модуля по её <c>archivePath</c> (как в манифесте,
    /// напр. "obs/assets/0/x.jpg"). Возвращает null, если записи нет или путь выходит
    /// за пределы данного модуля. Поток нужно освободить вызывающему.
    /// </summary>
    public required Func<string, Stream?> OpenModuleEntry { get; init; }

    /// <summary>Список archivePath всех записей этого модуля, присутствующих в архиве.</summary>
    public required IReadOnlyList<string> ModuleEntryPaths { get; init; }

    /// <summary>Записи этого модуля из манифеста.</summary>
    public required ModuleEntry ModuleEntry { get; init; }

    /// <summary>Папка, куда модуль складывает свои ассеты (выбор пользователя).</summary>
    public required string AssetsDirectory { get; init; }

    /// <summary>
    /// Если задан — конфиг распакован под эту папку (а не в реальные системные пути).
    /// Модуль использует это, чтобы найти восстановленные файлы (напр. сцены).
    /// </summary>
    public string? DestinationRootOverride { get; init; }
}

/// <summary>
/// Необязательный хук модуля, вызываемый движком ПОСЛЕ распаковки обычных записей.
/// Позволяет модулю разместить свои «управляемые» записи (<see cref="PathEntry.ManagedByModule"/>)
/// и выполнить пост-обработку — например, разложить ассеты OBS и переписать пути в сценах.
/// Вся специфика приложения остаётся в модуле; ядро не знает деталей.
/// </summary>
public interface IModuleRestoreHook
{
    Task RestoreAsync(ModuleRestoreContext context, CancellationToken ct = default);
}
