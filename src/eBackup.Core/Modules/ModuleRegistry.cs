using System.Text.Json;
using eBackup.Core.Abstractions;
using eBackup.Localization;

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

    /// <summary>
    /// Пользовательский выключатель: false — модуль не участвует в бэкапах
    /// (на восстановление НЕ влияет — restore-хуки работают всегда).
    /// </summary>
    public bool Enabled { get; init; } = true;

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
public sealed class ModuleRegistry(IReadOnlyList<IModuleSource> sources, string? disabledConfigPath = null)
{
    private readonly string _disabledPath = disabledConfigPath ?? ModulePaths.DisabledModulesPath;

    /// <summary>Все модули, включая неактивные (с Problem) — для list-modules / GUI.</summary>
    public IReadOnlyList<ModuleDescriptor> Discover()
    {
        var disabled = LoadDisabledIds();
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
                    Problem = L.Get("Core_SourceLoadFailed", ex.Message)
                });
                continue;
            }

            foreach (var d in found)
            {
                var withEnabled = d with { Enabled = !disabled.Contains(d.Id) };
                result.Add(seen.Add(d.Id)
                    ? withEnabled
                    : withEnabled with { Trust = ModuleTrust.Blocked, Problem = L.Get("Core_IdTakenByHigherPriority", d.Id) });
            }
        }

        return result;
    }

    /// <summary>Модули для БЭКАПА: готовые к работе и включённые пользователем.</summary>
    public IReadOnlyList<IBackupModule> LoadEnabled()
        => Discover()
            .Where(d => d.Problem is null && d.Trust == ModuleTrust.Trusted && d.Instance is not null && d.Enabled)
            .Select(d => d.Instance!)
            .ToList();

    /// <summary>
    /// Модули для ВОССТАНОВЛЕНИЯ: выключатель не учитывается — restore-хуки
    /// (например, раскладка ассетов OBS) должны отработать в любом случае.
    /// </summary>
    public IReadOnlyList<IBackupModule> LoadForRestore()
        => Discover()
            .Where(d => d.Problem is null && d.Trust == ModuleTrust.Trusted && d.Instance is not null)
            .Select(d => d.Instance!)
            .ToList();

    /// <summary>Включить/выключить модуль (сохраняется между запусками; общий для GUI и CLI).</summary>
    public void SetEnabled(string id, bool enabled)
    {
        var disabled = LoadDisabledIds();
        if (enabled)
            disabled.Remove(id);
        else
            disabled.Add(id);

        var dir = Path.GetDirectoryName(_disabledPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = _disabledPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(disabled.OrderBy(x => x, StringComparer.Ordinal).ToList()));
        File.Move(tmp, _disabledPath, overwrite: true);
    }

    private HashSet<string> LoadDisabledIds()
    {
        try
        {
            if (File.Exists(_disabledPath))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_disabledPath)) ?? [];
                return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // повреждённый файл — считаем, что выключенных нет
        }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}
