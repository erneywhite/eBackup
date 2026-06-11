using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using eBackup.Core.Security;
using eBackup.Storage.Sftp;

namespace eBackup.Storage;

/// <summary>
/// WebDAV-хранилище (Nextcloud, ownCloud, Яндекс.Диск и т.п.) на чистом HttpClient:
/// PROPFIND — листинг, PUT/GET — заливка/скачивание, DELETE, MKCOL — папки.
/// Аутентификация — Basic (пароль в DPAPI; для Яндекса — пароль приложения).
/// </summary>
public sealed class WebDavStorage(SavedStorage config, ISecretProtector protector)
    : IArchiveStorage, ISeekableArchiveStorage
{
    private static readonly HttpClient Http = new(new HttpClientHandler())
    {
        Timeout = TimeSpan.FromMinutes(30) // большие архивы по медленным каналам
    };

    private static readonly XNamespace Dav = "DAV:";

    public string Name => config.Name;

    private Uri FolderUri => BuildFolderUri(
        config.ServiceUrl ?? throw new InvalidOperationException("У WebDAV-хранилища не задан URL."),
        config.RemoteDirectory);

    /// <summary>База + относительная папка → абсолютный URL папки со слэшем на конце.</summary>
    internal static Uri BuildFolderUri(string baseUrl, string? remoteDir)
    {
        var url = baseUrl.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        url = url.TrimEnd('/');

        var dir = (remoteDir ?? string.Empty).Trim().Trim('/');
        if (dir.Length > 0 && dir != ".")
        {
            var encoded = string.Join('/', dir.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
            url += "/" + encoded;
        }

        return new Uri(url + "/");
    }

    private AuthenticationHeaderValue AuthHeader()
    {
        var password = config.ProtectedPassword is null
            ? string.Empty
            : protector.Unprotect(config.ProtectedPassword);
        var raw = $"{config.Username}:{password}";
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
    }

    private HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = AuthHeader();
        return request;
    }

    public async Task UploadAsync(string localFilePath, string remoteName, CancellationToken ct = default)
    {
        await EnsureFolderAsync(ct).ConfigureAwait(false);

        await using var stream = File.OpenRead(localFilePath);
        using var request = Request(HttpMethod.Put, new Uri(FolderUri, Uri.EscapeDataString(remoteName)));
        request.Content = new StreamContent(stream);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task DownloadAsync(string remoteName, string localFilePath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var request = Request(HttpMethod.Get, new Uri(FolderUri, Uri.EscapeDataString(remoteName)));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var target = File.Create(localFilePath);
        await response.Content.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    /// <summary>Чтение кусками через HTTP Range; сервер обязан отвечать 206.</summary>
    public async Task<Stream> OpenSeekableReadAsync(string remoteName, CancellationToken ct = default)
    {
        var uri = new Uri(FolderUri, Uri.EscapeDataString(remoteName));

        using var head = Request(HttpMethod.Head, uri);
        using var headResponse = await Http.SendAsync(head, ct).ConfigureAwait(false);
        headResponse.EnsureSuccessStatusCode();
        var length = headResponse.Content.Headers.ContentLength
            ?? throw new IOException("WebDAV-сервер не сообщил размер файла.");

        return new RangeStream(length, async (from, count, token) =>
        {
            using var request = Request(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(from, from + count - 1);
            using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw new IOException(
                    "WebDAV-сервер не поддерживает чтение по диапазонам (Range) — скачай архив целиком.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return await RangeStream.ReadBoundedAsync(stream, count, token).ConfigureAwait(false);
        });
    }

    public async Task<IReadOnlyList<RemoteFileInfo>> ListDetailedAsync(CancellationToken ct = default)
    {
        using var request = Request(new HttpMethod("PROPFIND"), FolderUri);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(
            """<?xml version="1.0"?><d:propfind xmlns:d="DAV:"><d:allprop/></d:propfind>""",
            Encoding.UTF8, "application/xml");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var result = new List<RemoteFileInfo>();

        foreach (var entry in xml.Descendants(Dav + "response"))
        {
            var href = entry.Element(Dav + "href")?.Value;
            if (href is null)
                continue;

            var prop = entry.Descendants(Dav + "prop").FirstOrDefault();
            if (prop is null || prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null)
                continue; // папки пропускаем (включая саму папку-родителя)

            var name = WebUtility.UrlDecode(href.TrimEnd('/').Split('/')[^1]);
            if (!name.EndsWith(".ebk", StringComparison.OrdinalIgnoreCase))
                continue;

            long.TryParse(prop.Element(Dav + "getcontentlength")?.Value, out var size);
            DateTime.TryParse(prop.Element(Dav + "getlastmodified")?.Value,
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var modified);

            result.Add(new RemoteFileInfo(name, size, modified.ToLocalTime()));
        }

        return result.OrderByDescending(f => f.LastWriteTime).ToList();
    }

    public async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Delete, new Uri(FolderUri, Uri.EscapeDataString(remoteName)));
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureFolderAsync(ct).ConfigureAwait(false);

            using var request = Request(new HttpMethod("PROPFIND"), FolderUri);
            request.Headers.Add("Depth", "0");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return ConnectionTestResult.Ok($"WebDAV доступен: {FolderUri}");
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Fail(ex.Message);
        }
    }

    /// <summary>Создаёт цепочку папок (MKCOL по сегментам); «уже существует» — не ошибка.</summary>
    private async Task EnsureFolderAsync(CancellationToken ct)
    {
        var baseUri = BuildFolderUri(config.ServiceUrl!, null);
        var dir = (config.RemoteDirectory ?? string.Empty).Trim().Trim('/');
        if (dir.Length == 0 || dir == ".")
            return;

        var current = baseUri;
        foreach (var segment in dir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = new Uri(current, Uri.EscapeDataString(segment) + "/");
            using var request = Request(new HttpMethod("MKCOL"), current);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            // 405 — уже существует; 301/302 — сервер указывает на существующую коллекцию.
            if (!response.IsSuccessStatusCode &&
                response.StatusCode is not (HttpStatusCode.MethodNotAllowed
                    or HttpStatusCode.MovedPermanently or HttpStatusCode.Found))
                response.EnsureSuccessStatusCode();
        }
    }
}
