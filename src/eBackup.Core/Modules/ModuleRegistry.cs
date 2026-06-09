using eBackup.Core.Abstractions;

namespace eBackup.Core.Modules;

/// <summary>Откуда взялся модуль. Словарь заморожен; ExternalDll зарезервирован под будущую DLL-загрузку.</summary>
public enum ModuleSource
{
    BuiltIn,
    Declarative,
    ExternalDll
}

/// <summary>Уровень доверия модулю.</summary>
public enum ModuleTrust
{
    Trusted,
    NeedsConsent,
    Blocked
}

/// <summary>
/// Описание модуля для реестра/UI. Если <see cref="Problem"/> != null — модуль показывается,
/// но не активируется. <see cref="Instance"/> заполнен для готовых к работе модулей.
/// </summary>
public sealed record ModuleDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required ModuleSource Source { get; init; }

    /// <summary>Происхождение (файл/папка) — для диагностики.</summary>
    public string? Origin { get; init; }

    /// <summary>Если задано — причина, по которой модуль неактивен.</summary>
    public string? Problem { get; init; }

    /// <summary>Готовый экземпляр (для встроенных и декларативных он создаётся сразу).</summary>
    public IBackupModule? Instance { get; init; }

    public ModuleTrust Trust { get; init; } = ModuleTrust.Trusted;

    /// <summary>Требуемая версия контракта (используется DLL-источником позже).</summary>
    public ApiVersion? RequiredApi { get; init; }
}

/// <summary>Источник модулей (встроенные, декларативные, в будущем — DLL). Не бросает за свою границу.</summary>
public interface IModuleSource
{
    ModuleSource Kind { get; }
    IEnumerable<ModuleDescriptor> Discover();
}

/// <summary>
/// Собирает модули из нескольких источников. Источники передаются В ПОРЯДКЕ ПРИОРИТЕТА
/// (встроенные раньше декларативных и т.д.): при конфликте id побеждает первый, остальные
/// помечаются Blocked, но остаются видимыми. Де-дубликация — ДО движка, поэтому
/// дубликат id не роняет восстановление.
/// </summary>
public sealed class ModuleRegistry(IReadOnlyList<IModuleSource> sources)
{
    /// <summary>Все модули, включая неактивные (с Problem) — для list-modules / GUI.</summary>
    public IReadOnlyList<ModuleDescriptor> Discover()
    {
        var result = new List<ModuleDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            List<ModuleDescriptor> found;
            try
            {
                found = source.Discover()
                    .OrderBy(d => d.Id, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex)
            {
                result.Add(new ModuleDescriptor
                {
                    Id = $"<{source.Kind.ToString().ToLowerInvariant()}>",
                    DisplayName = source.Kind.ToString(),
                    Source = source.Kind,
                    Trust = ModuleTrust.Blocked,
                    Problem = $"Источник не загрузился: {ex.Message}"
                });
                continue;
            }

            foreach (var d in found)
            {
                result.Add(seen.Add(d.Id)
                    ? d
                    : d with { Trust = ModuleTrust.Blocked, Problem = $"id '{d.Id}' уже занят модулем с более высоким приоритетом" });
            }
        }

        return result;
    }

    /// <summary>Готовые к работе модули (Trusted, без проблем) — для движка.</summary>
    public IReadOnlyList<IBackupModule> LoadEnabled()
        => Discover()
            .Where(d => d.Problem is null && d.Trust == ModuleTrust.Trusted && d.Instance is not null)
            .Select(d => d.Instance!)
            .ToList();
}
