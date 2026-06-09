using eBackup.Core.Abstractions;
using eBackup.Core.Model;

namespace eBackup.Core.Modules;

/// <summary>JSON-схема дескриптора декларативного модуля (*.module.json).</summary>
public sealed record DeclarativeModuleJson
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public string? MinApiVersion { get; init; }
    public List<DeclarativeEntryJson> Entries { get; init; } = [];
}

public sealed record DeclarativeEntryJson
{
    public string? TokenPath { get; init; }
    public PathEntryType Type { get; init; } = PathEntryType.File;
    public string? ArchivePath { get; init; }
    public List<string> ExcludeGlobs { get; init; } = [];
}

/// <summary>
/// Модуль из декларативного дескриптора: ТОЛЬКО данные (пути/маски), без кода и без
/// restore-хука. Безопасен к подбросу — его записи валидируются при загрузке источником.
/// </summary>
public sealed class DeclarativeModule(string id, string displayName, IReadOnlyList<PathEntry> entries) : IBackupModule
{
    public string Id => id;
    public string DisplayName => displayName;

    public Task<IReadOnlyList<PathEntry>> DiscoverAsync(CancellationToken ct = default)
        => Task.FromResult(entries);
}
