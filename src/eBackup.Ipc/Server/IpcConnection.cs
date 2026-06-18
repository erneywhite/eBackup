using eBackup.Ipc.Contracts;
using eBackup.Ipc.Transport;

namespace eBackup.Ipc.Server;

/// <summary>
/// Обслуживает ОДНО соединение поверх duplex-стрима: обмен преамбулами, затем цикл
/// «прочитать запрос → диспатч → записать ответ/ошибку». Любой транспортный сбой или
/// чистое закрытие завершают цикл. Чистая логика — проверяется по живому пайпу в тесте,
/// а accept-loop хоста под LocalSystem поверх неё придёт с самим Worker (S4c).
/// </summary>
public static class IpcConnection
{
    /// <summary>Лимит размера входящего (клиентского) фрейма — запросы небольшие.</summary>
    public const int MaxInboundFrameBytes = 256 * 1024;

    public static async Task ServeAsync(Stream stream, IIpcHandlers handlers, Func<CallerContext> resolveCaller, CancellationToken ct)
    {
        var reader = new FrameReader(stream, MaxInboundFrameBytes);
        using var writer = new FrameWriter(stream);

        // Асимметрия рукопожатия: клиент пишет преамбулу первым, сервер ЧИТАЕТ первым.
        // На пайпе с нулевым буфером запись ждёт чтения — будь обе стороны «пиши первым»,
        // вышла бы взаимоблокировка. Бонус: после первого чтения импперсонация клиента надёжна.
        await reader.ReadPreambleAsync(ct).ConfigureAwait(false);
        var caller = resolveCaller();
        await writer.WritePreambleAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            Frame? req;
            try
            {
                req = await reader.ReadFrameAsync(ct).ConfigureAwait(false);
            }
            catch (IpcProtocolException)
            {
                break; // битый фрейм → разрываем соединение
            }

            if (req is null) break; // клиент закрыл соединение

            var resp = await IpcDispatcher.DispatchAsync(req, handlers, caller, ct).ConfigureAwait(false);
            await writer.WriteFrameAsync(resp, ct).ConfigureAwait(false);
        }
    }
}
