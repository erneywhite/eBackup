using eBackup.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// eBackup-служба: под LocalSystem держит named-pipe и исполняет привилегированные бэкапы.
// Запускается и как Windows-служба, и как консоль (для отладки). Настоящие обработчики
// (JobManager + движок) подключатся на S4c-2/3 — пока accept-loop на заглушке.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "eBackup");
builder.Services.AddHostedService<IpcWorker>();
builder.Build().Run();
