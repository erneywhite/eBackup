namespace eBackup.Ipc.Transport;

/// <summary>
/// Нарушение транспортного протокола (битая преамбула, превышение размера фрейма, обрыв
/// посреди фрейма). Всегда ведёт к закрытию соединения — данными доверять нельзя.
/// </summary>
public sealed class IpcProtocolException : Exception
{
    public IpcProtocolException(string message) : base(message) { }
}
