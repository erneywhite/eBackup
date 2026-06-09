using System.IO.Compression;
using eBackup.Core.Model;

namespace eBackup.Core.Abstractions;

/// <summary>
/// Контекст для restore-хука модуля: доступ к архиву, записям модуля из манифеста,
/// папке для ассетов и (для тестов) альтернативному корню распаковки.
/// </summary>
public sealed class ModuleRestoreContext
{
    /// <summary>Открытый архив .ebk (для чтения данных управляемых записей).</summary>
    public required ZipArchive Archive { get; init; }

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
