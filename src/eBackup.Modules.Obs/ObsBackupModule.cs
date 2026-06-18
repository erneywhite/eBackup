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
/// Корень данных OBS (Roaming); по умолчанию %APPDATA%/obs-studio. Нужен для тестов.
/// </param>
/// <param name="installRootOverride">
/// Корень установки OBS; по умолчанию %PROGRAMFILES%/obs-studio. Нужен для тестов.
/// </param>
public sealed class ObsBackupModule(string? obsRootOverride = null, string? installRootOverride = null)
    : IBackupModule, IModuleRestoreHook, IUserScopedDiscovery
{
    // Резолвер источников по профилю ВЫЗВАВШЕГО (движок/служба внедряют его до DiscoverAsync).
    // Без него (GUI, тесты) — профиль процесса. Критично под службой: SYSTEM ≠ профиль юзера,
    // иначе сцены/ассеты ищутся в systemprofile и не находятся.
    private Func<string, string>? _resolveToken;

    public void UseSourceResolver(Func<string, string> resolveToken) => _resolveToken = resolveToken;

    // Корень данных OBS (Roaming) для ЧТЕНИЯ при обнаружении: override (тесты) > резолвер
    // (служба, per-SID) > профиль процесса (GUI). В МАНИФЕСТ всё равно идёт токен (переносимо).
    private string ObsRoot() => obsRootOverride
        ?? _resolveToken?.Invoke("{APPDATA}/obs-studio")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");

    private string InstallRoot() => installRootOverride
        ?? _resolveToken?.Invoke("{PROGRAMFILES}/obs-studio")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio");

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
        var obsRoot = ObsRoot();         // для ЧТЕНИЯ сцен/дедупа (per-SID под службой)
        var installRoot = InstallRoot();

        // Основная конфигурация OBS (профили, коллекции сцен, global.ini, service.json
        // с подключениями и стрим-ключами) лежит в %APPDATA%/obs-studio.
        var entries = new List<PathEntry>
        {
            new()
            {
                // В проде — токен (переносимо); при override (тесты) — конкретный путь.
                TokenPath = obsRootOverride is null ? "{APPDATA}/obs-studio" : obsRoot.Replace('\\', '/'),
                Type = PathEntryType.Directory,
                ArchivePath = "obs/obs-studio",
                ExcludeGlobs = Excludes
            }
        };

        // Бинарники и данные плагинов из папки установки (Program Files) — тянем целиком.
        AddPlugins(entries, installRoot);

        // Зависимые ассеты сцен (картинки/видео/слайдшоу/VLC), лежащие вне папки OBS.
        // Помечаем ManagedByModule — их разложит restore-хук модуля (As-2), а не движок.
        AddSceneAssets(entries, obsRoot, ct);

        return Task.FromResult<IReadOnlyList<PathEntry>>(entries);
    }

    private void AddPlugins(List<PathEntry> entries, string installRoot)
    {
        // obs-plugins (DLL плагинов) и data/obs-plugins (их данные) из папки установки.
        // ВНИМАНИЕ: восстановление сюда требует прав администратора (Program Files).
        var pluginBin = Path.Combine(installRoot, "obs-plugins");
        if (Directory.Exists(pluginBin))
            entries.Add(new PathEntry
            {
                TokenPath = installRootOverride is null
                    ? "{PROGRAMFILES}/obs-studio/obs-plugins"
                    : pluginBin.Replace('\\', '/'),
                Type = PathEntryType.Directory,
                ArchivePath = "obs/install/obs-plugins"
            });

        var pluginData = Path.Combine(installRoot, "data", "obs-plugins");
        if (Directory.Exists(pluginData))
            entries.Add(new PathEntry
            {
                TokenPath = installRootOverride is null
                    ? "{PROGRAMFILES}/obs-studio/data/obs-plugins"
                    : pluginData.Replace('\\', '/'),
                Type = PathEntryType.Directory,
                ArchivePath = "obs/install/data-obs-plugins"
            });
    }

    private static void AddSceneAssets(List<PathEntry> entries, string obsRoot, CancellationToken ct)
    {
        var scenesDir = Path.Combine(obsRoot, "basic", "scenes");
        if (!Directory.Exists(scenesDir))
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                        if (full.StartsWith(obsRoot, StringComparison.OrdinalIgnoreCase)) continue;

                        // Ассеты лежат в архиве плоско по имени файла; индекс-префикс —
                        // только при совпадении имён (чтобы не плодить папки «0», «1»…).
                        var fileName = Path.GetFileName(full);
                        while (fileName.Contains(".."))
                            fileName = fileName.Replace("..", ".");
                        var entryName = usedNames.Add(fileName) ? fileName : $"{index}-{fileName}";

                        entries.Add(new PathEntry
                        {
                            // Для ассетов храним ИСХОДНЫЙ путь как есть (не токенизируем):
                            // он нужен As-2, чтобы найти и переписать эту строку в scene JSON.
                            TokenPath = full.Replace('\\', '/'),
                            Type = PathEntryType.File,
                            ArchivePath = $"obs/assets/{entryName}",
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

    /// <summary>
    /// Restore-хук: раскладывает ассеты в выбранную пользователем папку и переписывает
    /// абсолютные пути в восстановленных scene JSON на новое расположение.
    /// </summary>
    public async Task RestoreAsync(ModuleRestoreContext context, CancellationToken ct = default)
    {
        // Восстановленные сцены: в проде — реальный obs-studio (restore идёт в GUI под юзером,
        // резолвер не внедрён → профиль процесса = юзер); при override — под ним.
        var scenesRoot = context.DestinationRootOverride is null
            ? ObsRoot()
            : Path.Combine(context.DestinationRootOverride, "obs", "obs-studio");
        var scenesDir = Path.Combine(scenesRoot, "basic", "scenes");

        // 1) Извлечь ассеты в выбранную папку, собрать карту «старый путь → новый».
        var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in context.ModuleEntry.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.ManagedByModule || entry.Type != PathEntryType.File)
                continue;

            using var src = context.OpenModuleEntry(entry.ArchivePath);
            if (src is null)
                continue;

            // "obs/assets/0/file.jpg" -> "<AssetsDirectory>/0/file.jpg"
            var rel = entry.ArchivePath.StartsWith("obs/assets/", StringComparison.Ordinal)
                ? entry.ArchivePath["obs/assets/".Length..]
                : Path.GetFileName(entry.ArchivePath);
            var dest = Path.Combine(context.AssetsDirectory, rel.Replace('/', Path.DirectorySeparatorChar));

            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            await using (var outFs = File.Create(dest))
                await src.CopyToAsync(outFs, ct).ConfigureAwait(false);

            remap[entry.TokenPath] = dest.Replace('\\', '/');
        }

        if (remap.Count == 0 || !Directory.Exists(scenesDir))
            return;

        // 2) Переписать пути в восстановленных scene JSON на новое расположение ассетов.
        foreach (var sceneFile in Directory.EnumerateFiles(scenesDir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(sceneFile, ct).ConfigureAwait(false);
            var updated = text;
            foreach (var (oldPath, newPath) in remap)
                updated = updated.Replace(oldPath, newPath, StringComparison.Ordinal);

            if (!string.Equals(updated, text, StringComparison.Ordinal))
                await File.WriteAllTextAsync(sceneFile, updated, ct).ConfigureAwait(false);
        }
    }
}
