using eBackup.Ipc.Client;

namespace eBackup.App;

public enum ServiceState { Disconnected, Connected, NotInstalled, Unavailable }

/// <summary>
/// Связь GUI со службой как единой программой: подключиться к работающей службе; если не отвечает —
/// попытаться её поднять и переподключиться; если не вышло — внятное состояние ошибки. Держит
/// единственный <see cref="IpcClient"/> на процесс GUI.
/// </summary>
public sealed class ServiceConnection
{
    /// <summary>Единое подключение на процесс GUI (страницы берут службу отсюда).</summary>
    public static ServiceConnection Shared { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);

    public ServiceState State { get; private set; } = ServiceState.Disconnected;
    public IpcClient? Client { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Гарантировать живое подключение к службе. true — подключены.</summary>
    public async Task<bool> EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (State == ServiceState.Connected && Client is not null) return true;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == ServiceState.Connected && Client is not null) return true;

            // 1) Уже работающая служба.
            if (await TryConnectAsync(ct).ConfigureAwait(false)) return true;

            // 2) Не отвечает — пытаемся поднять.
            if (!ServiceLauncher.IsInstalled())
            {
                State = ServiceState.NotInstalled;
                Error = "Служба eBackup не установлена. Переустановите eBackup.";
                return false;
            }
            if (await ServiceLauncher.TryStartAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false)
                && await TryConnectAsync(ct).ConfigureAwait(false))
                return true;

            State = ServiceState.Unavailable;
            Error ??= "Не удалось запустить службу eBackup.";
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(3)); // нет службы → не висим, падаем быстро
            Client?.Dispose();
            Client = await IpcPipeClient.ConnectAsync(ct: connectCts.Token).ConfigureAwait(false);
            State = ServiceState.Connected;
            Error = null;
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
    }
}
