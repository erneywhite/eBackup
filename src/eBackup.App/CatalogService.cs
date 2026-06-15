using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using eBackup.Platform;

namespace eBackup.App;

/// <summary>Одна запись каталога (из catalog/index.json).</summary>
public sealed record CatalogModule
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? Author { get; init; }
    public string? Version { get; init; }
    /// <summary>Путь файла модуля внутри каталога, напр. "modules/minecraft-java.module.json".</summary>
    public string File { get; init; } = "";
    /// <summary>Какие корни модуль трогает (для показа пользователю), напр. ["{APPDATA}"].</summary>
    public List<string> Tokens { get; init; } = [];
}

/// <summary>Разобранный catalog/index.json.</summary>
public sealed record CatalogIndex
{
    public int SchemaVersion { get; init; }
    public string? Updated { get; init; }
    public List<CatalogModule> Modules { get; init; } = [];
}

public enum CatalogSource { Network, Cache, None }

public sealed record CatalogResult
{
    public CatalogIndex? Index { get; init; }
    public CatalogSource Source { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Источник каталога модулей: тянет catalog/index.json сырым с GitHub (как UpdateService),
/// кэширует в %LOCALAPPDATA%/eBackup и при отсутствии сети отдаёт кэш. Сетевые ошибки не
/// валят UI — возвращаются как результат с источником/ошибкой.
/// </summary>
public static class CatalogService
{
    private const string RawBase =
        "https://raw.githubusercontent.com/erneywhite/eBackup/main/catalog/";
    private const string IndexUrl = RawBase + "index.json";

    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static string CachePath => AppPaths.CatalogCacheFile;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("eBackup", UpdateService.AppVersion()));
        return http;
    }

    /// <summary>
    /// Загрузить каталог. Сначала сеть (и обновляем кэш), при неудаче — последний кэш.
    /// Источник результата показывает, откуда данные (для подписи «оффлайн / устарело»).
    /// </summary>
    public static async Task<CatalogResult> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var jsonText = await Http.GetStringAsync(IndexUrl, ct).ConfigureAwait(false);
            var index = JsonSerializer.Deserialize<CatalogIndex>(jsonText, JsonOpts);
            if (index is not null)
            {
                TrySaveCache(jsonText);
                return new CatalogResult { Index = index, Source = CatalogSource.Network };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // нет сети / некорректный ответ — пробуем кэш ниже
        }

        try
        {
            if (File.Exists(CachePath))
            {
                var cached = JsonSerializer.Deserialize<CatalogIndex>(File.ReadAllText(CachePath), JsonOpts);
                if (cached is not null)
                    return new CatalogResult { Index = cached, Source = CatalogSource.Cache };
            }
        }
        catch
        {
            // повреждённый кэш — игнорируем
        }

        return new CatalogResult
        {
            Source = CatalogSource.None,
            Error = "не удалось загрузить каталог (нет сети и кэша)"
        };
    }

    /// <summary>Скачать сырой текст module.json записи каталога (для установки на шаге «Загрузить»).</summary>
    public static async Task<string> DownloadModuleJsonAsync(CatalogModule m, CancellationToken ct = default)
    {
        var rel = m.File.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(rel) || rel.Contains("..") ||
            rel.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("недопустимый путь файла модуля в каталоге");
        return await Http.GetStringAsync(RawBase + rel, ct).ConfigureAwait(false);
    }

    private static void TrySaveCache(string jsonText)
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, jsonText);
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch
        {
            // кэш необязателен
        }
    }
}
