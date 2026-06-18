using System.IO.Pipes;
using System.Security.Principal;

namespace eBackup.Ipc.Server;

/// <summary>
/// Устанавливает личность подключившегося клиента через RunAsClient (служба под SYSTEM
/// импперсонирует клиента и читает его SID). Все авторизационные решения делаются по этому
/// SID, а не по токену продолжения. Отвергаем служебные/сетевые/анонимные подключения —
/// со службой говорит только локальный интерактивный пользователь.
/// </summary>
public static class ClientIdentity
{
    public sealed record Result(string Sid, bool IsInteractive, bool IsAdmin, bool IsAcceptable);

    // Отвергаемые «не-человеческие» источники: SYSTEM/LocalService/NetworkService/NETWORK/ANONYMOUS.
    private static readonly HashSet<string> Rejected = ["S-1-5-18", "S-1-5-19", "S-1-5-20", "S-1-5-2", "S-1-5-7"];
    private const string InteractiveSid = "S-1-5-4";

    public static Result Resolve(NamedPipeServerStream pipe)
    {
        var sid = string.Empty;
        var interactive = false;
        var admin = false;

        pipe.RunAsClient(() =>
        {
            using var id = WindowsIdentity.GetCurrent();
            sid = id.User?.Value ?? string.Empty;
            interactive = id.Groups?.OfType<SecurityIdentifier>().Any(g => g.Value == InteractiveSid) ?? false;
            // Не-elevated админ → IsInRole(Administrator)=false (фильтрованный токен) — и трактуем как не-админа.
            admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        });

        var acceptable = sid.Length > 0 && !Rejected.Contains(sid);
        return new Result(sid, interactive, admin, acceptable);
    }

    public static CallerContext ToCaller(Result r) => new(r.Sid, r.IsAdmin);
}
