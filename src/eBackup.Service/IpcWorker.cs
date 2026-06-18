using eBackup.Ipc.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace eBackup.Service;

/// <summary>
/// Фоновая служба: держит accept-цикл named-pipe и обслуживает GUI-клиентов. Пока обработчики —
/// заглушка (<see cref="InMemoryHandlers"/>); настоящие (JobManager + движок + сторы) придут на S4c-2/3.
/// </summary>
public sealed class IpcWorker : BackgroundService
{
    private readonly ILogger<IpcWorker> _log;

    public IpcWorker(ILogger<IpcWorker> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(@"eBackup IPC: accept-цикл на \\.\pipe\{Pipe}", PipeSecurityFactory.DefaultPipeName);
        IIpcHandlers handlers = new InMemoryHandlers();
        try
        {
            await IpcPipeServer.RunAsync(handlers, stoppingToken,
                onError: ex => _log.LogWarning(ex, "IPC-соединение завершилось с ошибкой"));
        }
        catch (OperationCanceledException)
        {
            // штатная остановка службы
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Accept-цикл IPC аварийно завершился");
            throw;
        }
    }
}
