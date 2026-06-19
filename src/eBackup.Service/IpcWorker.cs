using eBackup.Ipc.Server;
using eBackup.Service.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace eBackup.Service;

/// <summary>
/// Фоновая служба: держит accept-цикл named-pipe и обслуживает GUI-клиентов настоящими
/// обработчиками из общего <see cref="ServicePipeline"/> (тот же JobManager/конфиг, что и у
/// ScheduleWorker). Сам конвейер строит и владеет им ServicePipeline.
/// </summary>
public sealed class IpcWorker : BackgroundService
{
    private readonly ILogger<IpcWorker> _log;
    private readonly ServicePipeline _pipeline;

    public IpcWorker(ILogger<IpcWorker> log, ServicePipeline pipeline)
    {
        _log = log;
        _pipeline = pipeline;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Восстановление после сбоя: пометить запуски, прерванные падением/перезапуском службы.
        var interrupted = await CrashRecovery.SweepInterruptedAsync(_pipeline.History);
        if (interrupted > 0)
            _log.LogInformation("Помечено прерванных запусков при старте: {Count}", interrupted);

        _log.LogInformation(@"eBackup IPC: accept-цикл на \\.\pipe\{Pipe}", PipeSecurityFactory.DefaultPipeName);

        try
        {
            await IpcPipeServer.RunAsync(_pipeline.Handlers, stoppingToken,
                onError: ex => _log.LogWarning(ex, "IPC-соединение завершилось с ошибкой"),
                jobStream: _pipeline.JobStream, archiveReader: _pipeline.ArchiveReader);
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
