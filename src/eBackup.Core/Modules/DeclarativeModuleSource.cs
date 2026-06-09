using System.Text.Json;
using System.Text.RegularExpressions;
using eBackup.Core.Abstractions;
using eBackup.Core.Model;

namespace eBackup.Core.Modules;

/// <summary>
/// Загружает декларативные модули из папки (по умолчанию %APPDATA%/eBackup/modules/*.module.json).
/// Это пользовательский контент → ЖЁСТКАЯ валидация при загрузке (граница безопасности —
/// ввод, а не папка). Безопасно по исполнению (кода нет), но НЕ exfiltration-free, поэтому
/// пути ограничены app-data корнями + денлист чувствительных файлов.
/// </summary>
public sealed partial class DeclarativeModuleSource(string? modulesDirectory = null) : IModuleSource
{
    private readonly string _dir = modulesDirectory ?? ModulePaths.ModulesDirectory;

    // Декларативным модулям разрешены ТОЛЬКО app-data корни (никаких профилей/Program Files/ключей).
    private static readonly string[] AllowedTokens = ["{APPDATA}", "{LOCALAPPDATA}", "{PROGRAMDATA}"];
    private static readonly string[] DenyGlobs =
        ["**/.ssh/**", "**/.aws/**", "**/.gnupg/**", "**/*.key", "**/*.pfx"];

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdRegex();

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
            return Blocked(fallbackId, file, $"не читается JSON: {ex.Message}");
        }

        if (json is null || string.IsNullOrWhiteSpace(json.Id))
            return Blocked(fallbackId, file, "нет id");

        var id = json.Id.Trim();
        if (!IdRegex().IsMatch(id))
            return Blocked(id, file, "недопустимый id (разрешено: a-z 0-9 . _ -, до 64 символов)");

        if (!string.IsNullOrWhiteSpace(json.MinApiVersion))
        {
            if (!ApiVersion.TryParse(json.MinApiVersion, out var min))
                return Blocked(id, file, $"неверная minApiVersion: {json.MinApiVersion}");
            if (min.CompareTo(ContractInfo.Current) > 0)
                return Blocked(id, file, $"требуется eBackup новее (контракт {min} > {ContractInfo.Current})");
        }

        var entries = new List<PathEntry>();
        foreach (var e in json.Entries)
        {
            if (e.Type == PathEntryType.RegistryKey)
                continue; // реестр движком пока не поддерживается — пропускаем

            if (string.IsNullOrWhiteSpace(e.TokenPath) || !IsAllowedToken(e.TokenPath!))
                return Blocked(id, file, $"путь вне разрешённых app-data корней: {e.TokenPath}");

            if (string.IsNullOrWhiteSpace(e.ArchivePath) || !IsSafeRelative(e.ArchivePath!))
                return Blocked(id, file, $"недопустимый archivePath: {e.ArchivePath}");

            IReadOnlyList<string> excludes = e.Type == PathEntryType.Directory
                ? e.ExcludeGlobs.Concat(DenyGlobs).ToList()
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
            return Blocked(id, file, "нет валидных записей");

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

    private static bool IsAllowedToken(string tokenPath)
    {
        if (tokenPath.Contains(".."))
            return false;
        return AllowedTokens.Any(t => tokenPath == t || tokenPath.StartsWith(t + "/", StringComparison.Ordinal));
    }

    private static bool IsSafeRelative(string archivePath)
        => !archivePath.Contains("..")
           && !archivePath.StartsWith('/')
           && !archivePath.StartsWith('\\')
           && !Path.IsPathRooted(archivePath);

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
