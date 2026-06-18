using System.IO.Pipes;

namespace eBackup.Ipc.Server;

/// <summary>
/// Accept-цикл named-pipe для службы: создаёт инстансы с защищённым DACL, на каждом подключении
/// устанавливает личность клиента и обслуживает соединение через <see cref="IpcConnection"/>.
/// Соединения обслуживаются конкурентно. На первом инстансе FirstPipeInstance защищает от
/// сквоттинга имени; занятое имя на старте — фатально (поднимаем наружу).
/// </summary>
public static class IpcPipeServer
{
    public static async Task RunAsync(IIpcHandlers handlers, CancellationToken ct, Action<Exception>? onError = null, IJobStream? jobStream = null)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = PipeSecurityFactory.CreateServerStream(firstInstance: first);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                if (first) throw; // имя занято на первом инстансе = возможный сквоттинг — падаем громко
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            first = false;

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }

            _ = ServeOneAsync(pipe, handlers, onError, ct, jobStream); // конкурентно, не блокируем приём следующих
        }
    }

    private static async Task ServeOneAsync(
        NamedPipeServerStream pipe, IIpcHandlers handlers, Action<Exception>? onError, CancellationToken ct, IJobStream? jobStream)
    {
        try
        {
            await IpcConnection.ServeAsync(pipe, handlers, () =>
            {
                var id = ClientIdentity.Resolve(pipe);
                if (!id.IsAcceptable)
                    throw new UnauthorizedAccessException($"Отклонён вызывающий (SID {id.Sid}).");
                return ClientIdentity.ToCaller(id);
            }, ct, jobStream).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex); // один кривой клиент не должен ронять службу
        }
        finally
        {
            try { pipe.Dispose(); } catch { }
        }
    }
}
