using System.Text.Json;
using eBackup.Core.Abstractions;
using eBackup.Core.Model;
using eBackup.Core.Paths;
using eBackup.Localization;

namespace eBackup.Core.Modules;

/// <summary>
/// Загружает декларативные модули из папки (по умолчанию %APPDATA%/eBackup/modules/*.module.json).
/// Это пользовательский контент → ЖЁСТКАЯ валидация при загрузке (граница безопасности —
/// ввод, а не папка). Безопасно по исполнению (кода нет), но НЕ exfiltration-free, поэтому
/// пути ограничены app-data корнями + денлист чувствительных файлов.
/// </summary>
public sealed class DeclarativeModuleSource(string? modulesDirectory = null) : IModuleSource
{
    private readonly string _dir = modulesDirectory ?? ModulePaths.ModulesDirectory;

    // eBackup — инструмент бэкапа: модуль вправе собирать ЛЮБЫЕ пути (ssh-ключи, папки вне
    // app-data и т.д.). Это безопасно по дизайну: модуль НЕ задаёт хранилище-цель — он лишь
    // перечисляет, ЧТО собрать; куда уйдёт архив, выбирает пользователь. Доверие к каталогу —
    // через ревью PR; личные модули человек держит у себя. Единственная проверка источника —
    // запрет «..» (остальной containment живёт на ВОССТАНОВЛЕНИИ — это анти-zip-slip, не лимит).
    public ModuleSource Kind => ModuleSource.Declarative;

    public IEnumerable<ModuleDescriptor> Discover()
    {
        if (!Directory.Exists(_dir))
            yield break;

        foreach (var file in Directory.EnumerateFiles(_dir, "*.module.json"))
            yield return LoadOne(file);
    }

    private ModuleDescriptor LoadOne(string file)
    {
        var fallbackId = Path.GetFileName(file);

        DeclarativeModuleJson? json;
        try
        {
            json = JsonSerializer.Deserialize<DeclarativeModuleJson>(File.ReadAllText(file), ManifestJson.Options);
        }
        catch (Exception ex)
        {
            return Blocked(fallbackId, file, L.Get("Core_ModuleJsonUnreadable", ex.Message));
        }

        if (json is null || string.IsNullOrWhiteSpace(json.Id))
            return Blocked(fallbackId, file, L.Get("Core_ModuleNoId"));

        var id = json.Id.Trim();
        if (!ModuleValidation.IsValidId(id))
            return Blocked(id, file, L.Get("Core_ModuleInvalidId"));

        if (!string.IsNullOrWhiteSpace(json.MinApiVersion))
        {
            if (!ApiVersion.TryParse(json.MinApiVersion, out var min))
                return Blocked(id, file, L.Get("Core_ModuleBadMinApiVersion", json.MinApiVersion));
            if (min.CompareTo(ContractInfo.Current) > 0)
                return Blocked(id, file, L.Get("Core_ModuleNeedsNewerEBackup", min, ContractInfo.Current));
        }

        var entries = new List<PathEntry>();
        foreach (var e in json.Entries)
        {
            if (e.Type == PathEntryType.RegistryKey)
                continue; // реестр движком пока не поддерживается — пропускаем

            if (string.IsNullOrWhiteSpace(e.TokenPath) || PathTokens.HasTraversal(e.TokenPath!))
                return Blocked(id, file, L.Get("Core_ModuleBadPath", e.TokenPath));

            if (string.IsNullOrWhiteSpace(e.ArchivePath) || !ModuleValidation.IsSafeArchivePath(e.ArchivePath))
                return Blocked(id, file, L.Get("Core_ModuleBadArchivePath", e.ArchivePath));

            IReadOnlyList<string> excludes = e.Type == PathEntryType.Directory
                ? e.ExcludeGlobs
                : [];

            entries.Add(new PathEntry
            {
                TokenPath = e.TokenPath!,
                Type = e.Type,
                // Запись модуля всегда под data/<id>/ — для заужения доступа и де-дубликации.
                ArchivePath = id + "/" + e.ArchivePath!.Replace('\\', '/').TrimStart('/'),
                ExcludeGlobs = excludes,
                ManagedByModule = false   // декларативный модуль НИКОГДА не управляет restore
            });
        }

        if (entries.Count == 0)
            return Blocked(id, file, L.Get("Core_ModuleNoValidEntries"));

        var name = string.IsNullOrWhiteSpace(json.DisplayName) ? id : json.DisplayName!.Trim();
        return new ModuleDescriptor
        {
            Id = id,
            DisplayName = name,
            Source = ModuleSource.Declarative,
            Origin = file,
            Trust = ModuleTrust.Trusted,
            Instance = new DeclarativeModule(id, name, entries)
        };
    }

    private static ModuleDescriptor Blocked(string id, string file, string problem) => new()
    {
        Id = id,
        DisplayName = id,
        Source = ModuleSource.Declarative,
        Origin = file,
        Trust = ModuleTrust.Blocked,
        Problem = problem
    };
}
