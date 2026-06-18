using System.IO.Pipes;
using System.Security.Principal;
using eBackup.Ipc.Server;

namespace eBackup.Ipc.Client;

/// <summary>
/// Подключение GUI к named-pipe службы. ДО любого обмена (тем более секретов) проверяет, что
/// пайп принадлежит LocalSystem: не-админ не может создать пайп с владельцем SYSTEM, так что
/// чужой/подменённый сервер отсекается (анти-сквоттинг).
/// </summary>
public static class IpcPipeClient
{
    private const string LocalSystemSid = "S-1-5-18";

    public static async Task<IpcClient> ConnectAsync(
        string pipeName = PipeSecurityFactory.DefaultPipeName, CancellationToken ct = default)
    {
        var stream = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        try
        {
            await stream.ConnectAsync(ct).ConfigureAwait(false);
            VerifyServerIsLocalSystem(stream);
            return await IpcClient.StartAsync(stream, ct).ConfigureAwait(false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void VerifyServerIsLocalSystem(NamedPipeClientStream stream)
    {
        var owner = stream.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || owner.Value != LocalSystemSid)
            throw new UnauthorizedAccessException(
                "Сервер named-pipe не принадлежит LocalSystem — возможна подмена. Отказ.");
    }
}
