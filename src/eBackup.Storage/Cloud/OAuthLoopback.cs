using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace eBackup.Storage.Cloud;

/// <summary>
/// Десктопный OAuth: PKCE + loopback-редирект. Открывает браузер, поднимает
/// HttpListener на localhost и ждёт код авторизации.
/// </summary>
public static class OAuthLoopback
{
    public sealed record AuthCode(string Code, string RedirectUri, string CodeVerifier);

    public static async Task<AuthCode> AuthorizeAsync(
        string authorizationEndpoint,
        IReadOnlyDictionary<string, string> queryParams,
        int? fixedPort = null,
        string? providerName = null,
        CancellationToken ct = default)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        // Метка попытки: на фиксированном порту (Dropbox) редирект от УСТАРЕВШЕЙ
        // вкладки входа иначе достался бы новой попытке с чужим PKCE-кодом.
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var port = fixedPort ?? GetFreePort();
        var redirect = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new IOException(
                $"Порт {port} уже занят (возможно, ждёт ответа предыдущая попытка входа " +
                "или запущена вторая копия eBackup). Попробуй ещё раз через минуту.", ex);
        }
        try
        {
            var query = new StringBuilder();
            foreach (var (key, value) in queryParams)
                query.Append(Uri.EscapeDataString(key)).Append('=')
                     .Append(Uri.EscapeDataString(value)).Append('&');
            query.Append("redirect_uri=").Append(Uri.EscapeDataString(redirect))
                 .Append("&code_challenge=").Append(challenge)
                 .Append("&code_challenge_method=S256")
                 .Append("&state=").Append(state);

            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationEndpoint + "?" + query,
                UseShellExecute = true
            });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(3));

            HttpListenerContext context;
            while (true)
            {
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ct.ThrowIfCancellationRequested(); // внешняя отмена — не таймаут
                    throw new TimeoutException("Вход не подтверждён в браузере (таймаут 3 минуты).");
                }

                if (context.Request.QueryString["state"] == state)
                    break;

                // Устаревшая вкладка входа или посторонний запрос (favicon и т.п.) —
                // отвечаем и продолжаем ждать «свой» редирект.
                await RespondAsync(context, Page("dim", "↻", "Устаревшая вкладка входа",
                    "Закрой её и подтверди вход в последней открытой вкладке.",
                    tryCloseTab: false)).ConfigureAwait(false);
            }

            var code = context.Request.QueryString["code"];
            var error = context.Request.QueryString["error"];

            var who = providerName is null ? "Аккаунт" : providerName + "-аккаунт";
            await RespondAsync(context, code is null
                ? Page("err", "✕", "Вход не выполнен",
                    "Авторизация отклонена — вернись в eBackup и попробуй ещё раз. Вкладку можно закрыть.",
                    tryCloseTab: true)
                : Page("ok", "✓", "Готово!",
                    $"{who} подключён к eBackup. Вкладку можно закрыть.",
                    tryCloseTab: true)).ConfigureAwait(false);

            if (code is null)
                throw new InvalidOperationException("Авторизация отклонена: " + (error ?? "код не получен"));

            return new AuthCode(code, redirect, verifier);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, string html)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            context.Response.Close();
        }
        catch
        {
            // Клиент оборвал соединение — на исход входа не влияет.
        }
    }

    /// <summary>
    /// Страница-ответ в браузере, в стиле приложения (тёмная «аврора» + карточка).
    /// kind: ok — градиентный бейдж, err — красный, dim — нейтральный.
    /// </summary>
    private static string Page(string kind, string glyph, string title, string message, bool tryCloseTab)
    {
        // Закрытие вкладки самой себя браузер может и запретить (скриптам не всегда
        // позволено закрывать вкладки, открытые пользователем) — текст остаётся фолбэком.
        var script = tryCloseTab
            ? "<script>window.open('','_self');window.close();" +
              "setTimeout(function(){window.close()},300);</script>"
            : "";
        return $$"""
<!doctype html><html lang="ru"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>eBackup — вход</title><style>
*{box-sizing:border-box}
body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
font-family:"Segoe UI",system-ui,sans-serif;color:#EFEAF6;background:
radial-gradient(640px 420px at 18% 8%,rgba(201,125,246,.16),transparent 60%),
radial-gradient(520px 380px at 84% 22%,rgba(255,109,200,.13),transparent 60%),
radial-gradient(760px 540px at 50% 105%,rgba(124,94,255,.12),transparent 60%),#0E0A16}
.card{background:#171022;border:1px solid rgba(255,255,255,.08);border-radius:20px;
padding:44px 52px;margin:16px;text-align:center;max-width:430px;box-shadow:0 24px 80px rgba(0,0,0,.45)}
.badge{width:64px;height:64px;border-radius:50%;margin:0 auto 20px;display:flex;
align-items:center;justify-content:center;font-size:28px;font-weight:700}
.badge.ok{background:linear-gradient(135deg,#C97DF6,#FF6DC8);color:#1A0F22}
.badge.err{background:rgba(255,138,156,.12);color:#FF8A9C;border:1px solid rgba(255,138,156,.35)}
.badge.dim{background:rgba(255,255,255,.06);color:#A89BC0;border:1px solid rgba(255,255,255,.1)}
h1{font-size:22px;font-weight:600;margin:0 0 10px}
p{margin:0;color:#A89BC0;font-size:14px;line-height:1.55}
.brand{margin-top:26px;font-size:12px;letter-spacing:.4px;color:#6F6390}
.brand b{font-weight:700;background:linear-gradient(90deg,#C97DF6,#FF6DC8);
-webkit-background-clip:text;background-clip:text;color:transparent}
</style></head><body><div class="card">
<div class="badge {{kind}}">{{glyph}}</div><h1>{{title}}</h1><p>{{message}}</p>
<div class="brand"><b>eBackup</b> — бэкапы, которые переезжают с тобой</div>
</div>{{script}}</body></html>
""";
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
