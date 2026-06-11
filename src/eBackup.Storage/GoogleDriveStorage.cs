using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using eBackup.Core.Security;
using eBackup.Storage.Cloud;
using eBackup.Storage.Sftp;

namespace eBackup.Storage;

/// <summary>
/// Google Drive через REST (scope drive.file — приложение видит ТОЛЬКО свои файлы).
/// Архивы лежат в папке (по умолчанию «eBackup») в корне Диска; загрузка — resumable
/// (без ограничения «мелких» аплоадов), одноимённый файл заменяется.
/// </summary>
public sealed class GoogleDriveStorage(SavedStorage config, ISecretProtector protector) : IArchiveStorage
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string ApiBase = "https://www.googleapis.com/drive/v3";
    private const string FolderMime = "application/vnd.google-apps.folder";

    private string? _accessToken;
    private string? _folderId;

    public string Name => config.Name;

    private string FolderName =>
        string.IsNullOrWhiteSpace(config.RemoteDirectory) || config.RemoteDirectory == "."
            ? "eBackup"
            : config.RemoteDirectory!.Trim();

    // ---------- вход (вызывается из UI) ----------

    /// <summary>Открывает браузер, возвращает refresh-токен (хранить зашифрованным).</summary>
    public static async Task<string> AuthorizeAsync(CancellationToken ct = default)
    {
        var auth = await OAuthLoopback.AuthorizeAsync(
            "https://accounts.google.com/o/oauth2/v2/auth",
            new Dictionary<string, string>
            {
                ["client_id"] = CloudOAuthConfig.GoogleClientId,
                ["response_type"] = "code",
                ["scope"] = "https://www.googleapis.com/auth/drive.file",
                ["access_type"] = "offline",
                ["prompt"] = "consent" // иначе Google может не выдать refresh_token повторно
            }, ct: ct).ConfigureAwait(false);

        using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["code"] = auth.Code,
                ["client_id"] = CloudOAuthConfig.GoogleClientId,
                ["client_secret"] = CloudOAuthConfig.GoogleClientSecret,
                ["redirect_uri"] = auth.RedirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = auth.CodeVerifier
            }), ct).ConfigureAwait(false);

        var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("refresh_token", out var refresh))
            throw new InvalidOperationException(
                "Google не вернул refresh-токен. Отзови доступ eBackup на myaccount.google.com/permissions и войди заново.");
        return refresh.GetString()!;
    }

    // ---------- IArchiveStorage ----------

    public async Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default)
    {
        var folderId = await FolderIdAsync(createIfMissing: true, ct).ConfigureAwait(false);

        // Перезапись: Drive разрешает дубликаты имён — старый файл удаляем.
        var existing = await FindFileIdAsync(folderId, remoteName, ct).ConfigureAwait(false);
        if (existing is not null)
            await SendAsync(HttpMethod.Delete, $"{ApiBase}/files/{existing}", null, ct).ConfigureAwait(false);

        // Resumable-загрузка: init с метаданными → PUT содержимого по выданному URL.
        var initRequest = new HttpRequestMessage(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { name = remoteName, parents = new[] { folderId } }),
                Encoding.UTF8, "application/json")
        };
        initRequest.Headers.Authorization = await BearerAsync(ct).ConfigureAwait(false);
        using var initResponse = await Http.SendAsync(initRequest, ct).ConfigureAwait(false);
        await ThrowIfErrorAsync(initResponse, ct).ConfigureAwait(false);
        var uploadUrl = initResponse.Headers.Location
            ?? throw new IOException("Google Drive не выдал URL загрузки.");

        await using var stream = File.OpenRead(localFilePath);
        using var putRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = new StreamContent(stream)
        };
        putRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var putResponse = await Http.SendAsync(putRequest, ct).ConfigureAwait(false);
        await ThrowIfErrorAsync(putResponse, ct).ConfigureAwait(false);
    }

    public async Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default)
    {
        var folderId = await FolderIdAsync(createIfMissing: false, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Папка «{FolderName}» на Диске не найдена.");
        var fileId = await FindFileIdAsync(folderId, remoteName, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Файл {remoteName} не найден на Google Drive.");

        var dir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/files/{fileId}?alt=media");
        request.Headers.Authorization = await BearerAsync(ct).ConfigureAwait(false);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, ct).ConfigureAwait(false);
        await using var target = File.Create(localFilePath);
        await response.Content.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteFileInfo>> ListDetailedAsync(CancellationToken ct = default)
    {
        var folderId = await FolderIdAsync(createIfMissing: false, ct).ConfigureAwait(false);
        if (folderId is null)
            return [];

        var result = new List<RemoteFileInfo>();
        string? pageToken = null;
        do
        {
            var url = $"{ApiBase}/files?q={Uri.EscapeDataString($"'{folderId}' in parents and trashed=false")}" +
                      "&fields=nextPageToken,files(name,size,modifiedTime)&pageSize=1000" +
                      (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var json = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);

            foreach (var file in json.RootElement.GetProperty("files").EnumerateArray())
            {
                var name = file.GetProperty("name").GetString()!;
                if (!name.EndsWith(".ebk", StringComparison.OrdinalIgnoreCase))
                    continue;

                long.TryParse(file.TryGetProperty("size", out var s) ? s.GetString() : null, out var size);
                var modified = file.TryGetProperty("modifiedTime", out var m) &&
                               DateTime.TryParse(m.GetString(), out var dt)
                    ? dt.ToLocalTime()
                    : DateTime.MinValue;
                result.Add(new RemoteFileInfo(name, size, modified));
            }

            pageToken = json.RootElement.TryGetProperty("nextPageToken", out var t) ? t.GetString() : null;
        } while (pageToken is not null);

        return result.OrderByDescending(f => f.LastWriteTime).ToList();
    }

    public async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        var folderId = await FolderIdAsync(createIfMissing: false, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Папка «{FolderName}» на Диске не найдена.");
        var fileId = await FindFileIdAsync(folderId, remoteName, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Файл {remoteName} не найден на Google Drive.");
        await SendAsync(HttpMethod.Delete, $"{ApiBase}/files/{fileId}", null, ct).ConfigureAwait(false);
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
    {
        try
        {
            await FolderIdAsync(createIfMissing: true, ct).ConfigureAwait(false);
            return ConnectionTestResult.Ok($"Google Drive: папка «{FolderName}» готова.");
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Fail(ex.Message);
        }
    }

    // ---------- внутренности ----------

    private async Task<AuthenticationHeaderValue> BearerAsync(CancellationToken ct)
    {
        if (_accessToken is null)
        {
            var refreshToken = protector.Unprotect(config.ProtectedOAuthToken
                ?? throw new InvalidOperationException("Не выполнен вход в Google — открой хранилище и войди."));

            using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = CloudOAuthConfig.GoogleClientId,
                    ["client_secret"] = CloudOAuthConfig.GoogleClientSecret,
                    ["refresh_token"] = refreshToken,
                    ["grant_type"] = "refresh_token"
                }), ct).ConfigureAwait(false);
            var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            _accessToken = json.RootElement.GetProperty("access_token").GetString();
        }
        return new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private async Task<string?> FolderIdAsync(bool createIfMissing, CancellationToken ct)
    {
        if (_folderId is not null)
            return _folderId;

        var q = $"mimeType='{FolderMime}' and name='{EscapeQuery(FolderName)}' and trashed=false";
        var json = await SendAsync(HttpMethod.Get,
            $"{ApiBase}/files?q={Uri.EscapeDataString(q)}&fields=files(id)", null, ct).ConfigureAwait(false);
        var first = json.RootElement.GetProperty("files").EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Object)
            return _folderId = first.GetProperty("id").GetString();

        if (!createIfMissing)
            return null;

        var created = await SendAsync(HttpMethod.Post, $"{ApiBase}/files",
            new { name = FolderName, mimeType = FolderMime }, ct).ConfigureAwait(false);
        return _folderId = created.RootElement.GetProperty("id").GetString();
    }

    private async Task<string?> FindFileIdAsync(string folderId, string name, CancellationToken ct)
    {
        var q = $"'{folderId}' in parents and name='{EscapeQuery(name)}' and trashed=false";
        var json = await SendAsync(HttpMethod.Get,
            $"{ApiBase}/files?q={Uri.EscapeDataString(q)}&fields=files(id)", null, ct).ConfigureAwait(false);
        var first = json.RootElement.GetProperty("files").EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object ? first.GetProperty("id").GetString() : null;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = await BearerAsync(ct).ConfigureAwait(false);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task ThrowIfErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new IOException($"Google Drive: {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body)}");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Google Drive: {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body)}");
        return JsonDocument.Parse(body.Length == 0 ? "{}" : body);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private static string EscapeQuery(string value) => value.Replace(@"\", @"\\").Replace("'", @"\'");
}
