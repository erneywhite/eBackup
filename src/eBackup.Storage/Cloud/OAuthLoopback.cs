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
                await RespondAsync(context,
                    "Это устаревшая вкладка входа — закрой её и подтверди вход в последней открытой вкладке.")
                    .ConfigureAwait(false);
            }

            var code = context.Request.QueryString["code"];
            var error = context.Request.QueryString["error"];

            await RespondAsync(context, code is null
                    ? "Вход не выполнен. Вкладку можно закрыть."
                    : "Готово! Возвращайся в eBackup — вкладку можно закрыть.",
                tryCloseTab: true).ConfigureAwait(false);

            if (code is null)
                throw new InvalidOperationException("Авторизация отклонена: " + (error ?? "код не получен"));

            return new AuthCode(code, redirect, verifier);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, string message, bool tryCloseTab = false)
    {
        try
        {
            // Попытка закрыть вкладку сама по себе: браузер может и отказать
            // (скриптам не всегда позволено закрывать чужие вкладки) — тогда
            // остаётся текст с просьбой закрыть вручную.
            var script = tryCloseTab
                ? "<script>window.open('','_self');window.close();" +
                  "setTimeout(function(){window.close()},300);</script>"
                : "";
            var html = Encoding.UTF8.GetBytes(
                $"<html><meta charset=\"utf-8\"><body style=\"font-family:sans-serif\">{message}{script}</body></html>");
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(html, CancellationToken.None).ConfigureAwait(false);
            context.Response.Close();
        }
        catch
        {
            // Клиент оборвал соединение — на исход входа не влияет.
        }
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
