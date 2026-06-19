using eBackup.Core.Abstractions;
using eBackup.Core.Model;

namespace eBackup.Modules.VTubeStudio;

/// <summary>
/// Модуль бэкапа VTube Studio (Steam, appid 1325860). Игра лежит в Steam-библиотеке
/// (C:\Program Files (x86)\Steam или кастомный диск) — путь токеном не выразить, поэтому находим её
/// smart-discovery через Steam (<see cref="SteamLibraries"/>: HKLM + libraryfolders.vdf, работает
/// под службой). Забираем ТОЛЬКО пользовательский контент из VTube Studio_Data/StreamingAssets
/// (модели, предметы, фоны, конфиги/сцены), НЕ трогая тяжёлые трекеры (MXTracker/OpenSeeFace ~3.8 ГБ)
/// и шипованное (Language/Logs) — оно вернётся переустановкой игры. Whitelist-подход: берём только
/// известные пользовательские папки, чтобы новая тяжёлая папка трекера не утащилась случайно.
/// </summary>
/// <param name="streamingAssetsOverride">Путь к StreamingAssets напрямую (для тестов); иначе — дискавери.</param>
public sealed class VTubeStudioBackupModule(string? streamingAssetsOverride = null) : IBackupModule
{
    public string Id => "vtube-studio";
    public string DisplayName => "VTube Studio";

    private const string CommonFolder = "VTube Studio";

    // Папки пользовательского контента под StreamingAssets — забираем целиком (размер не экономим).
    private static readonly string[] UserContent =
    [
        "Config",          // vts_config.json, сцены (items_in_scene.json), калибровки, плагины, кастом-параметры
        "Live2DModels",    // модели
        "Items",           // предметы/реквизит
        "Backgrounds",     // фоны
        "Effects",
        "WorkshopCredits", // атрибуция мастерской
        "Backup",          // авто-копии конфигов самой VTS — тоже пользовательское, мелочь
    ];

    /// <summary>Найти папку StreamingAssets: override (тесты) > дискавери через Steam. null — VTS не найдена.</summary>
    private string? StreamingAssets()
    {
        if (streamingAssetsOverride is not null)
            return Directory.Exists(streamingAssetsOverride) ? streamingAssetsOverride : null;

        var common = SteamLibraries.FindAppCommonDir(CommonFolder);
        if (common is null)
            return null;
        var sa = Path.Combine(common, "VTube Studio_Data", "StreamingAssets");
        return Directory.Exists(sa) ? sa : null;
    }

    public Task<IReadOnlyList<PathEntry>> DiscoverAsync(CancellationToken ct = default)
    {
        var entries = new List<PathEntry>();

        var sa = StreamingAssets();
        if (sa is null)
            return Task.FromResult<IReadOnlyList<PathEntry>>(entries); // VTS не установлена — бэкапить нечего

        foreach (var sub in UserContent)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.Combine(sa, sub);
            if (!Directory.Exists(dir))
                continue;

            entries.Add(new PathEntry
            {
                // Абсолютный путь Steam-папки (токеном не выразить). Restore идёт на тот же путь;
                // кросс-машинный re-discovery — отдельный шаг на потом.
                TokenPath = dir.Replace('\\', '/'),
                Type = PathEntryType.Directory,
                ArchivePath = "vtube-studio/" + sub.ToLowerInvariant(),
                ExcludeGlobs = ["**/*.log"],
            });
        }

        return Task.FromResult<IReadOnlyList<PathEntry>>(entries);
    }
}
