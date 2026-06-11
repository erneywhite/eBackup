using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace eBackup.App;

/// <summary>Результат проверки обновлений: доступна ли новая версия и где её взять.</summary>
public sealed record UpdateInfo(string Version, string Url, string? Notes);

/// <summary>
/// Проверка обновлений через публичный GitHub Releases API (как в eCoda).
/// Сравнивает текущую версию сборки с последним релизом. Без своего сервера,
/// без скачивания и установки — только подсказка со ссылкой. Сетевые/парсинговые
/// ошибки гасятся: проверка обновлений никогда не мешает работе приложения.
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/erneywhite/eBackup/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub требует User-Agent; явный заголовок версии API — на будущее.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("eBackup", AppVersion()));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    public static string AppVersion()
        => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>null — обновления нет, проверка не удалась или релизов ещё нет.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null; // 404 — релизов ещё нет; прочее — молча пропускаем

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                return null;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            var latest = ParseVersion(tag);
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                ?? new Version(0, 0, 0);
            if (latest is null || latest <= current)
                return null;

            var url = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            return new UpdateInfo(
                latest.ToString(),
                url ?? "https://github.com/erneywhite/eBackup/releases/latest",
                string.IsNullOrWhiteSpace(notes) ? null : notes);
        }
        catch
        {
            return null; // оффлайн, таймаут, кривой ответ — не наша забота
        }
    }

    /// <summary>«v0.1.2» / «0.1» → Version; мусор → null.</summary>
    internal static Version? ParseVersion(string tag)
    {
        var cleaned = tag.Trim().TrimStart('v', 'V');
        // Берём только ведущую числовую часть («1.2.3-beta» → «1.2.3»).
        var end = 0;
        while (end < cleaned.Length && (char.IsDigit(cleaned[end]) || cleaned[end] == '.'))
            end++;
        cleaned = cleaned[..end];

        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        // Version требует минимум major.minor — добиваем нулями.
        var nums = new int[Math.Max(2, Math.Min(parts.Length, 4))];
        for (var i = 0; i < parts.Length && i < nums.Length; i++)
            if (!int.TryParse(parts[i], out nums[i]))
                return null;
        return nums.Length switch
        {
            2 => new Version(nums[0], nums[1]),
            3 => new Version(nums[0], nums[1], nums[2]),
            _ => new Version(nums[0], nums[1], nums[2], nums[3])
        };
    }
}
