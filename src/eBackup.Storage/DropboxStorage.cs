using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using eBackup.Core.Security;
using eBackup.Storage.Cloud;
using eBackup.Storage.Sftp;

namespace eBackup.Storage;

/// <summary>
/// Dropbox через REST (доступ «App folder» — приложение видит только Apps/&lt;имя&gt;).
/// Файлы больше ~100 МБ заливаются сессией по кускам (лимит одиночного аплоада — 150 МБ).
/// </summary>
public sealed class DropboxStorage(SavedStorage config, ISecretProtector protector)
    : IArchiveStorage, ISeekableArchiveStorage
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private const string TokenEndpoint = "https://api.dropboxapi.com/oauth2/token";
    private const long SingleUploadLimit = 100L * 1024 * 1024;
    private const int ChunkSize = 64 * 1024 * 1024;

    private string? _accessToken;

    public string Name => config.Name;

    private string Prefix => NormalizePath(config.RemoteDirectory);

    /// <summary>«"."/""» → «"" (корень app-папки)», «sub/dir/» → «/sub/dir».</summary>
    internal static string NormalizePath(string? dir)
    {
        var d = (dir ?? string.Empty).Trim().Trim('/');
        return d.Length == 0 || d == "." ? string.Empty : "/" + d;
    }

    private string FilePath(string name) => $"{Prefix}/{name}";

    // ---------- вход (вызывается из UI) ----------

    /// <summary>Открывает браузер, возвращает refresh-токен (хранить зашифрованным).</summary>
    public static async Task<string> AuthorizeAsync(CancellationToken ct = default)
    {
        var auth = await OAuthLoopback.AuthorizeAsync(
            "https://www.dropbox.com/oauth2/authorize",
            new Dictionary<string, string>
            {
                ["client_id"] = CloudOAuthConfig.DropboxAppKey,
                ["response_type"] = "code",
                ["token_access_type"] = "offline"
            },
            fixedPort: CloudOAuthConfig.DropboxRedirectPort, providerName: "Dropbox", ct: ct).ConfigureAwait(false);

        using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["code"] = auth.Code,
                ["grant_type"] = "authorization_code",
                ["client_id"] = CloudOAuthConfig.DropboxAppKey,
                ["redirect_uri"] = auth.RedirectUri,
                ["code_verifier"] = auth.CodeVerifier
            }), ct).ConfigureAwait(false);

        var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        return json.RootElement.GetProperty("refresh_token").GetString()!;
    }

    // ---------- IArchiveStorage ----------

    public async Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default)
    {
        var size = new FileInfo(localFilePath).Length;
        await using var stream = File.OpenRead(localFilePath);

        if (size <= SingleUploadLimit)
        {
            using var response = await ContentRequestAsync("https://content.dropboxapi.com/2/files/upload",
                new { path = FilePath(remoteName), mode = "overwrite", mute = true },
                new StreamContent(stream), ct).ConfigureAwait(false);
            await ThrowIfErrorAsync(response, ct).ConfigureAwait(false);
            return;
        }

        // Сессия по кускам.
        var buffer = new byte[ChunkSize];
        var read = await ReadChunkAsync(stream, buffer, ct).ConfigureAwait(false);
        string sessionId;
        using (var start = await ContentRequestAsync("https://content.dropboxapi.com/2/files/upload_session/start",
                   new { close = false },
                   new ByteArrayContent(buffer, 0, read), ct).ConfigureAwait(false))
        {
            var json = await ReadJsonAsync(start, ct).ConfigureAwait(false);
            sessionId = json.RootElement.GetProperty("session_id").GetString()!;
        }

        long offset = read;
        while ((read = await ReadChunkAsync(stream, buffer, ct).ConfigureAwait(false)) > 0)
        {
            using var append = await ContentRequestAsync(
                "https://content.dropboxapi.com/2/files/upload_session/append_v2",
                new { cursor = new { session_id = sessionId, offset }, close = false },
                new ByteArrayContent(buffer, 0, read), ct).ConfigureAwait(false);
            await ThrowIfErrorAsync(append, ct).ConfigureAwait(false);
            offset += read;
        }

        using var finish = await ContentRequestAsync(
            "https://content.dropboxapi.com/2/files/upload_session/finish",
            new
            {
                cursor = new { session_id = sessionId, offset },
                commit = new { path = FilePath(remoteName), mode = "overwrite", mute = true }
            },
            new ByteArrayContent([]), ct).ConfigureAwait(false);
        await ThrowIfErrorAsync(finish, ct).ConfigureAwait(false);
    }

    public async Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var response = await ContentRequestAsync("https://content.dropboxapi.com/2/files/download",
            new { path = FilePath(remoteName) }, content: null, ct).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, ct).ConfigureAwait(false);
        await using var target = File.Create(localFilePath);
        await response.Content.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    /// <summary>Чтение кусками: download с заголовком Range — архив не качается целиком.</summary>
    public async Task<Stream> OpenSeekableReadAsync(string remoteName, CancellationToken ct = default)
    {
        var meta = await RpcAsync("https://api.dropboxapi.com/2/files/get_metadata",
            new { path = FilePath(remoteName) }, ct).ConfigureAwait(false);
        var length = meta.RootElement.GetProperty("size").GetInt64();

        return new RangeStream(length, async (from, count, token) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://content.dropboxapi.com/2/files/download");
            request.Headers.Authorization = await BearerAsync(token).ConfigureAwait(false);
            request.Headers.Add("Dropbox-API-Arg",
                JsonSerializer.Serialize(new { path = FilePath(remoteName) }));
            request.Headers.Range = new RangeHeaderValue(from, from + count - 1);
            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw new IOException("Dropbox не вернул запрошенный диапазон (Range).");
            return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        });
    }

    public async Task<IReadOnlyList<RemoteFileInfo>> ListDetailedAsync(CancellationToken ct = default)
    {
        var result = new List<RemoteFileInfo>();
        var json = await RpcAsync("https://api.dropboxapi.com/2/files/list_folder",
            new { path = Prefix, limit = 2000 }, ct).ConfigureAwait(false);

        while (true)
        {
            foreach (var entry in json.RootElement.GetProperty("entries").EnumerateArray())
            {
                if (entry.GetProperty(".tag").GetString() != "file")
                    continue;
                var name = entry.GetProperty("name").GetString()!;
                if (!name.EndsWith(".ebk", StringComparison.OrdinalIgnoreCase))
                    continue;

                var size = entry.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                var modified = entry.TryGetProperty("server_modified", out var m) &&
                               DateTime.TryParse(m.GetString(), out var dt)
                    ? dt.ToLocalTime()
                    : DateTime.MinValue;
                result.Add(new RemoteFileInfo(name, size, modified));
            }

            if (json.RootElement.GetProperty("has_more").GetBoolean())
            {
                var cursor = json.RootElement.GetProperty("cursor").GetString();
                json = await RpcAsync("https://api.dropboxapi.com/2/files/list_folder/continue",
                    new { cursor }, ct).ConfigureAwait(false);
                continue;
            }
            break;
        }

        return result.OrderByDescending(f => f.LastWriteTime).ToList();
    }

    public async Task DeleteAsync(string remoteName, CancellationToken ct = default)
        => await RpcAsync("https://api.dropboxapi.com/2/files/delete_v2",
            new { path = FilePath(remoteName) }, ct).ConfigureAwait(false);

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
    {
        try
        {
            if (Prefix.Length > 0)
            {
                // Создаём подпапку, «уже существует» — не ошибка.
                using var response = await RpcRawAsync("https://api.dropboxapi.com/2/files/create_folder_v2",
                    new { path = Prefix }, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
                    await ThrowIfErrorAsync(response, ct).ConfigureAwait(false);
            }

            await RpcAsync("https://api.dropboxapi.com/2/files/list_folder",
                new { path = Prefix, limit = 1 }, ct).ConfigureAwait(false);
            return ConnectionTestResult.Ok("Dropbox: папка приложения доступна.");
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
                ?? throw new InvalidOperationException("Не выполнен вход в Dropbox — открой хранилище и войди."));

            using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = CloudOAuthConfig.DropboxAppKey
                }), ct).ConfigureAwait(false);
            var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            _accessToken = json.RootElement.GetProperty("access_token").GetString();
        }
        return new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    /// <summary>RPC-вызов (api.dropboxapi.com): JSON-аргументы в теле.</summary>
    private async Task<JsonDocument> RpcAsync(string url, object args, CancellationToken ct)
    {
        using var response = await RpcRawAsync(url, args, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> RpcRawAsync(string url, object args, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(args), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = await BearerAsync(ct).ConfigureAwait(false);
        return await Http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Content-вызов (content.dropboxapi.com): аргументы в заголовке, данные в теле.</summary>
    private async Task<HttpResponseMessage> ContentRequestAsync(
        string url, object args, HttpContent? content, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content ?? new ByteArrayContent([])
        };
        request.Headers.Authorization = await BearerAsync(ct).ConfigureAwait(false);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(args)); // non-ASCII экранируется по умолчанию
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadChunkAsync(FileStream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private static async Task ThrowIfErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new IOException($"Dropbox: {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body)}");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Dropbox: {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body)}");
        return JsonDocument.Parse(body.Length == 0 ? "{}" : body);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
