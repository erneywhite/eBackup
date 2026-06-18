using System.ServiceProcess;

namespace eBackup.App;

/// <summary>
/// Управление Windows-службой eBackup из GUI: проверить статус и попытаться запустить.
/// Запуск под обычным пользователем сработает, только если установщик дал интерактивным
/// пользователям право SERVICE_START; иначе вернёт false (GUI покажет ошибку). В норме служба
/// и так на автозапуске, поэтому это путь для редкого случая «служба остановлена».
/// </summary>
public static class ServiceLauncher
{
    public const string ServiceName = "eBackup";

    public static bool IsInstalled()
    {
        try { using var sc = new ServiceController(ServiceName); _ = sc.Status; return true; }
        catch { return false; }
    }

    public static bool IsRunning()
    {
        try { using var sc = new ServiceController(ServiceName); return sc.Status == ServiceControllerStatus.Running; }
        catch { return false; }
    }

    /// <summary>Запустить службу и дождаться Running. true — удалось (или уже запущена).</summary>
    public static async Task<bool> TryStartAsync(TimeSpan timeout)
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running) return true;
            if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, timeout)).ConfigureAwait(false);
            sc.Refresh();
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false; // не установлена / нет прав / таймаут
        }
    }
}
