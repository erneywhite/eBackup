using System.Reflection;
using eBackup.Core.History;
using eBackup.Core.Modules;
using eBackup.Core.Scheduling;
using eBackup.Ipc.Server;
using eBackup.Modules.Obs;
using eBackup.Platform;
using eBackup.Security;
using eBackup.Service.Handlers;
using eBackup.Service.Jobs;
using eBackup.Storage;

namespace eBackup.Service;

/// <summary>
/// Композиционный корень службы: строит весь конвейер ОДИН раз (история, реестр модулей, хранилища,
/// расписания, «свои папки», движки бэкапа/восстановления, единый <see cref="JobManager"/>) и делится
/// им между <see cref="IpcWorker"/> (named-pipe) и ScheduleWorker (таймер) — чтобы оба писали в ОДНУ
/// очередь задач и ОДИН машинный конфиг. Регистрируется синглтоном; хост зовёт DisposeAsync на остановке.
/// </summary>
public sealed class ServicePipeline : IAsyncDisposable
{
    public HistoryStore History { get; }
    public ModuleRegistry Registry { get; }
    public StorageStore Storages { get; }
    public ScheduleStore Schedules { get; }
    public CustomFolderStore Folders { get; }
    public JobManager Jobs { get; }
    public PassphraseVault Vault { get; }
    public IIpcHandlers Handlers { get; }
    public IJobStream JobStream { get; }
    public IArchiveReader ArchiveReader { get; }

    public ServicePipeline()
    {
        History = new HistoryStore();

        // Модули/конфиг служба берёт из ProgramData (под SYSTEM per-user профиль = системный, пуст).
        Registry = new ModuleRegistry(
        [
            new BuiltInModuleSource([new ObsBackupModule()]),
            new DeclarativeModuleSource(AppPaths.MachineModulesDir),
        ], disabledConfigPath: AppPaths.MachineDisabledModulesFile);

        // Один машинный протектор на хранилища И расписания (SYSTEM расшифрует секреты офлайн).
        var machineProtector = new MachineKeyProtector(new MachineKeyStore());
        Storages = new StorageStore(machineProtector, AppPaths.MachineStoragesFile,
            Path.Combine(AppPaths.MachineConfigDir, "connections.json"));
        Schedules = new ScheduleStore(machineProtector, AppPaths.MachineSchedulesFile);
        Folders = new CustomFolderStore();

        var backupRunner = new BackupRunner(
            // Список id в запросе авторитетен: интерактивный бэкап (GUI отдаёт только включённые) и
            // бэкап по расписанию (своя выборка, глобальный выключатель её не трогает) резолвим из ВСЕХ
            // рабочих модулей. Выключатель — это фильтр того, что ПРЕДЛАГАЕТСЯ, а не запрет на исполнение.
            ids => Registry.LoadForRestore().Where(m => ids.Contains(m.Id)).ToList(),
            resolveFolders: ids => ids.Where(Folders.Contains).ToList(),
            storages: Storages);
        var restoreRunner = new RestoreRunner(
            Storages,
            () => Registry.Discover().Where(d => d.Problem is null && d.Instance is not null).Select(d => d.Instance!).ToList());

        var historyWriter = new JobHistoryWriter(History);
        Jobs = new JobManager(
            backupRunner,
            channelFactory: runId => new JobChannel(History, runId),
            onStateChanged: historyWriter.OnStateChanged,
            restoreRunner: restoreRunner);

        Vault = new PassphraseVault(); // разовые тикеты фраз для интерактивного шифрования

        var build = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        Handlers = new ServiceHandlers(Jobs, History, Registry, Storages, Guid.NewGuid().ToString("N"), build,
            folders: Folders, schedules: Schedules, vault: Vault);
        JobStream = new JobStreamAdapter(Jobs);
        ArchiveReader = new ServiceArchiveReader(Storages);
    }

    public async ValueTask DisposeAsync()
    {
        Vault.Dispose(); // занулить все оставшиеся фразы
        await Jobs.DisposeAsync().ConfigureAwait(false);
    }
}
