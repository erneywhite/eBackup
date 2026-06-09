using eBackup.Core.Abstractions;
using eBackup.Core.Model;

namespace eBackup.Modules.Obs;

/// <summary>
/// Модуль бэкапа OBS Studio. Декларативная часть путей описана в obs.module.json,
/// а этот код-хук находит реальные расположения на текущей машине.
/// </summary>
public sealed class ObsBackupModule : IBackupModule
{
    public string Id => "obs";
    public string DisplayName => "OBS Studio";

    // Что НЕ тащим в бэкап: логи, дампы, профайлер, кэш апдейтера и тяжёлые кэши
    // браузерного источника (Chromium). Реальные настройки плагинов остаются.
    // Эти маски — специфика OBS и намеренно живут только в этом модуле.
    private static readonly string[] Excludes =
    [
        "logs/**",
        "crashes/**",
        "profiler_data/**",
        "updates/**",
        "plugin_config/obs-browser/Cache/**",
        "plugin_config/obs-browser/Code Cache/**",
        "plugin_config/obs-browser/GPUCache/**",
        "plugin_config/obs-browser/GrShaderCache/**",
        "plugin_config/obs-browser/Crashpad/**"
    ];

    public Task<IReadOnlyList<PathEntry>> DiscoverAsync(CancellationToken ct = default)
    {
        // Основная конфигурация OBS (профили, коллекции сцен, global.ini, service.json
        // с подключениями и стрим-ключами) лежит в %APPDATA%/obs-studio.
        // Это покрывает сцены, настройки и подключения.
        var entries = new List<PathEntry>
        {
            new()
            {
                TokenPath = "{APPDATA}/obs-studio",
                Type = PathEntryType.Directory,
                ArchivePath = "obs/obs-studio",
                ExcludeGlobs = Excludes
            }
        };

        // TODO(v1+): автопоиск установки OBS (uninstall-ключ реестра / запущенный
        // процесс) и опциональное включение пользовательских плагинов.
        return Task.FromResult<IReadOnlyList<PathEntry>>(entries);
    }
}
