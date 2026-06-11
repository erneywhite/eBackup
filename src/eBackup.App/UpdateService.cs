using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace eBackup.App;

public enum UpdateStage { Idle, Checking, UpToDate, Available, Downloading, ReadyToInstall, Failed }

/// <summary>Снимок состояния обновления — для баннера и карточки в настройках.</summary>
public sealed class UpdateState
{
    public UpdateStage Stage { get; init; } = UpdateStage.Idle;
    public string? Version { get; init; }
    public string? AssetUrl { get; init; }
    public long AssetSize { get; init; }
    public string? InstallerPath { get; init; }
    public double Progress { get; init; }   // 0..1 при скачивании
    public string? Error { get; init; }
}

/// <summary>
/// Обновление через публичный GitHub Releases (как в eCoda): проверка при запуске,
/// скачивание установщика-ассета с прогрессом и запуск его с перезапуском приложения.
/// Без своего сервера. Сетевые ошибки гасятся — обновление не мешает работе.
/// Состояние общее (баннер + карточка настроек подписываются на <see cref="Changed"/>).
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/erneywhite/eBackup/releases/latest";
    private const string ReleasesPage = "https://github.com/erneywhite/eBackup/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    public static event Action<UpdateState>? Changed;
    public static UpdateState Current { get; private set; } = new();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) }; // установщик может быть крупным
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("eBackup", AppVersion()));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    public static string AppVersion()
        => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static string ReleasesUrl => ReleasesPage;

    private static void Set(UpdateState state)
    {
        Current = state;
        Changed?.Invoke(state);
    }

    /// <summary>Проверить наличие новой версии. quiet=true — фоновая проверка при старте.</summary>
    public static async Task CheckAsync(bool quiet)
    {
        if (Current.Stage is UpdateStage.Checking or UpdateStage.Downloading)
            return;
        Set(new UpdateState { Stage = UpdateStage.Checking });

        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 — релизов ещё нет; для фоновой — тихо, для ручной — «актуально».
                Set(new UpdateState { Stage = UpdateStage.UpToDate });
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;

            if ((root.TryGetProperty("draft", out var d) && d.GetBoolean()) ||
                (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()))
            {
                Set(new UpdateState { Stage = UpdateStage.UpToDate });
                return;
            }

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var latest = tag is null ? null : ParseVersion(tag);
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                ?? new Version(0, 0, 0);
            if (latest is null || latest <= current)
            {
                Set(new UpdateState { Stage = UpdateStage.UpToDate });
                return;
            }

            // Ассет-установщик: eBackup-setup-*.exe.
            string? assetUrl = null;
            long assetSize = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is null ||
                        !name.StartsWith("eBackup-setup", StringComparison.OrdinalIgnoreCase) ||
                        !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        continue;
                    assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    assetSize = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    break;
                }
            }

            Set(new UpdateState
            {
                Stage = UpdateStage.Available,
                Version = latest.ToString(),
                AssetUrl = assetUrl,
                AssetSize = assetSize
            });
        }
        catch
        {
            Set(quiet
                ? new UpdateState { Stage = UpdateStage.Idle }
                : new UpdateState { Stage = UpdateStage.Failed, Error = "не удалось проверить обновления (нет сети?)" });
        }
    }

    /// <summary>Скачать установщик в temp с прогрессом. Возвращает путь или null при неудаче.</summary>
    public static async Task<string?> DownloadAsync()
    {
        var available = Current;
        if (available.Stage != UpdateStage.Available || available.AssetUrl is null)
            return null;

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "eBackup", "update");
            Directory.CreateDirectory(dir);
            // Старые скачанные установщики не копим.
            foreach (var old in Directory.EnumerateFiles(dir, "*.exe"))
                try { File.Delete(old); } catch { }

            var path = Path.Combine(dir, $"eBackup-setup-{available.Version}-x64.exe");

            using var response = await Http.GetAsync(
                available.AssetUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? available.AssetSize;

            await using (var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var dst = File.Create(path))
            {
                var buffer = new byte[256 * 1024];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                        Set(new UpdateState
                        {
                            Stage = UpdateStage.Downloading,
                            Version = available.Version,
                            AssetUrl = available.AssetUrl,
                            AssetSize = total,
                            Progress = Math.Clamp((double)done / total, 0, 1)
                        });
                }
            }

            // Базовая проверка целостности: размер должен совпасть с заявленным.
            if (total > 0 && new FileInfo(path).Length != total)
                throw new IOException("скачанный установщик неполный (размер не совпал)");

            Set(new UpdateState
            {
                Stage = UpdateStage.ReadyToInstall,
                Version = available.Version,
                InstallerPath = path
            });
            return path;
        }
        catch (Exception ex)
        {
            Set(new UpdateState
            {
                Stage = UpdateStage.Failed,
                Version = available.Version,
                AssetUrl = available.AssetUrl,
                AssetSize = available.AssetSize,
                Error = "не удалось скачать обновление: " + ex.Message
            });
            return null;
        }
    }

    /// <summary>
    /// Запустить скачанный установщик тихо (с перезапуском приложения через [Run] установщика)
    /// и вернуть true, если процесс стартовал — вызывающий должен сразу закрыть приложение,
    /// чтобы установщик мог заменить файлы.
    /// </summary>
    public static bool LaunchInstaller()
    {
        var path = Current.InstallerPath;
        if (path is null || !File.Exists(path))
            return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                // /SILENT — прогресс без страниц мастера; CLOSEAPPLICATIONS+RESTARTAPPLICATIONS —
                // через Restart Manager закрыть и (через [Run]) перезапустить приложение.
                Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /SUPPRESSMSGBOXES",
                UseShellExecute = true // нужен для UAC (установщик требует прав администратора)
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>«v0.1.2» / «0.1» → Version; мусор → null.</summary>
    internal static Version? ParseVersion(string tag)
    {
        var cleaned = tag.Trim().TrimStart('v', 'V');
        var end = 0;
        while (end < cleaned.Length && (char.IsDigit(cleaned[end]) || cleaned[end] == '.'))
            end++;
        cleaned = cleaned[..end];

        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
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
