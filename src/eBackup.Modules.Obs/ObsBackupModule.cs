using System.Text.Json;
using eBackup.Core.Abstractions;
using eBackup.Core.Model;

namespace eBackup.Modules.Obs;

/// <summary>
/// Модуль бэкапа OBS Studio. Декларативная часть путей описана в obs.module.json,
/// а этот код-хук находит реальные расположения на текущей машине, включая
/// зависимые ассеты сцен (картинки/медиа), которые лежат ВНЕ папки OBS.
/// </summary>
/// <param name="obsRootOverride">
/// Корень данных OBS; по умолчанию %APPDATA%/obs-studio. Параметр нужен в основном
/// для тестов (подсунуть временную папку со сценой).
/// </param>
public sealed class ObsBackupModule(string? obsRootOverride = null) : IBackupModule
{
    private readonly string _obsRoot = obsRootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");

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

        // Зависимые ассеты сцен (картинки/видео/слайдшоу/VLC), лежащие вне папки OBS.
        // Помечаем ManagedByModule — их разложит restore-хук модуля (As-2), а не движок.
        AddSceneAssets(entries, ct);

        return Task.FromResult<IReadOnlyList<PathEntry>>(entries);
    }

    private void AddSceneAssets(List<PathEntry> entries, CancellationToken ct)
    {
        var scenesDir = Path.Combine(_obsRoot, "basic", "scenes");
        if (!Directory.Exists(scenesDir))
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var sceneFile in Directory.EnumerateFiles(scenesDir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(sceneFile));
            }
            catch (JsonException)
            {
                continue; // нестандартный/битый файл — пропускаем
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("sources", out var sources) ||
                    sources.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var source in sources.EnumerateArray())
                {
                    foreach (var rawPath in ExtractAssetPaths(source))
                    {
                        string full;
                        try { full = Path.GetFullPath(rawPath); }
                        catch { continue; }

                        if (!seen.Add(full)) continue;
                        if (!File.Exists(full)) continue;

                        // Файлы внутри папки OBS и так попадут в основной каталог — не дублируем.
                        if (full.StartsWith(_obsRoot, StringComparison.OrdinalIgnoreCase)) continue;

                        entries.Add(new PathEntry
                        {
                            // Для ассетов храним ИСХОДНЫЙ путь как есть (не токенизируем):
                            // он нужен As-2, чтобы найти и переписать эту строку в scene JSON.
                            TokenPath = full.Replace('\\', '/'),
                            Type = PathEntryType.File,
                            ArchivePath = $"obs/assets/{index}/{Path.GetFileName(full)}",
                            ManagedByModule = true
                        });
                        index++;
                    }
                }
            }
        }
    }

    /// <summary>Вытащить пути к локальным файлам из настроек источника по его типу.</summary>
    private static IEnumerable<string> ExtractAssetPaths(JsonElement source)
    {
        if (!source.TryGetProperty("settings", out var s) || s.ValueKind != JsonValueKind.Object)
            yield break;

        var type = source.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;

        switch (type)
        {
            case "image_source":
                if (TryStr(s, "file", out var file))
                    yield return file;
                break;

            case "ffmpeg_source":
                // local_file — только если это локальный файл, а не URL.
                var isLocal = !s.TryGetProperty("is_local_file", out var il) || il.ValueKind != JsonValueKind.False;
                if (isLocal && TryStr(s, "local_file", out var lf))
                    yield return lf;
                break;

            case "slideshow":
            case "slideshow_v2":
                if (s.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                    foreach (var item in files.EnumerateArray())
                        if (TryStr(item, "value", out var v))
                            yield return v;
                break;

            case "vlc_source":
                if (s.TryGetProperty("playlist", out var playlist) && playlist.ValueKind == JsonValueKind.Array)
                    foreach (var item in playlist.EnumerateArray())
                        if (TryStr(item, "value", out var v))
                            yield return v;
                break;
        }
        // TODO(As-2+): фильтры источников (image_path у масок/LUT) — добавить позже.
    }

    private static bool TryStr(JsonElement obj, string prop, out string value)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(prop, out var el) &&
            el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                value = s;
                return true;
            }
        }
        value = string.Empty;
        return false;
    }
}
