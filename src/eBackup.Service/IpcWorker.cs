using System.Reflection;
using eBackup.Core.History;
using eBackup.Core.Modules;
using eBackup.Ipc.Server;
using eBackup.Modules.Obs;
using eBackup.Service.Handlers;
using eBackup.Service.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace eBackup.Service;

/// <summary>
/// Фоновая служба: собирает рабочий конвейер (реестр модулей → BackupRunner → JobManager → журнал)
/// и держит accept-цикл named-pipe, обслуживая GUI-клиентов настоящими обработчиками.
/// </summary>
public sealed class IpcWorker : BackgroundService
{
    private readonly ILogger<IpcWorker> _log;

    public IpcWorker(ILogger<IpcWorker> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var history = new HistoryStore();

        // Восстановление после сбоя: пометить запуски, прерванные падением/перезапуском службы.
        var interrupted = await CrashRecovery.SweepInterruptedAsync(history);
        if (interrupted > 0)
            _log.LogInformation("Помечено прерванных запусков при старте: {Count}", interrupted);

        var registry = new ModuleRegistry(
        [
            new BuiltInModuleSource([new ObsBackupModule()]),
            new DeclarativeModuleSource(),
        ]);
        var runner = new BackupRunner(
            ids => registry.LoadEnabled().Where(m => ids.Contains(m.Id)).ToList());
        var historyWriter = new JobHistoryWriter(history);

        await using var jobs = new JobManager(
            runner,
            channelFactory: runId => new JobChannel(history, runId),
            onStateChanged: historyWriter.OnStateChanged);

        var build = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var handlers = new ServiceHandlers(jobs, history, registry, Guid.NewGuid().ToString("N"), build);
        var jobStream = new JobStreamAdapter(jobs);

        _log.LogInformation(@"eBackup IPC: accept-цикл на \\.\pipe\{Pipe} (build {Build})",
            PipeSecurityFactory.DefaultPipeName, build);

        try
        {
            await IpcPipeServer.RunAsync(handlers, stoppingToken,
                onError: ex => _log.LogWarning(ex, "IPC-соединение завершилось с ошибкой"),
                jobStream: jobStream);
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
