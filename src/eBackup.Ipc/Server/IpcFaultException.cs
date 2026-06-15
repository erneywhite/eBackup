using eBackup.Ipc.Contracts;

namespace eBackup.Ipc.Server;

/// <summary>
/// Обработчик бросает её, чтобы вернуть клиенту типизированную ошибку (код из
/// <see cref="IpcErrorCodes"/>). Диспетчер превращает её в fault-фрейм. Любое ДРУГОЕ
/// исключение становится generic Internal — детали наружу через границу не уходят.
/// </summary>
public sealed class IpcFaultException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }

    public IpcFaultException(string code, string message, bool retryable = false) : base(message)
    {
        Code = code;
        Retryable = retryable;
    }
}
