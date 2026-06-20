using eBackup.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// eBackup-служба: под LocalSystem держит named-pipe и исполняет привилегированные бэкапы.
// Запускается и как Windows-служба, и как консоль (для отладки). Обработчики (JobManager + движок)
// собираются в ServicePipeline, accept-loop в IpcWorker обслуживает их вживую.
// Машинно-уникальный инстанс: защита от двойного запуска (например, перекрытие при апгрейде).
// В консольной отладке под обычным юзером нет SeCreateGlobalPrivilege — тогда мягко пропускаем.
Mutex? instanceLock = null;
try
{
    instanceLock = new Mutex(initiallyOwned: true, @"Global\eBackup.service", out var createdNew);
    if (!createdNew)
    {
        Console.Error.WriteLine("eBackup: служба уже запущена — выходим.");
        return;
    }
}
catch (Exception)
{
    // Нет привилегии на Global-объект (консоль под обычным юзером) — единственность не навязываем.
}

// Язык строк службы/ядра (фазы/журнал/ошибки) — из машинного конфига (по умолчанию русский);
// GUI обновит его по Hello. Применяем до старта воркеров, чтобы плановые прогоны шли на нужном языке.
ServiceLanguage.ApplyPersisted();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "eBackup");
builder.Services.AddSingleton<ServicePipeline>();   // общий конвейер для IPC- и Schedule-воркеров
// Расписаниям и IPC нужна ОДНА очередь задач и ОДИН конфиг — отдаём их из общего конвейера.
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServicePipeline>().Schedules);
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServicePipeline>().Jobs);
builder.Services.AddSingleton<IIdleSource, SessionIdleSource>(); // WTS-сеансы Session 0 + монитор нагрузки
builder.Services.AddHostedService<IpcWorker>();
builder.Services.AddHostedService<ScheduleWorker>();
builder.Build().Run();

GC.KeepAlive(instanceLock);
