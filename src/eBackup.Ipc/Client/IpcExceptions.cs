using eBackup.Ipc.Contracts;

namespace eBackup.Ipc.Client;

/// <summary>Служба вернула типизированную ошибку (fault) на запрос.</summary>
public sealed class IpcRequestException : Exception
{
    public IpcError Error { get; }

    public IpcRequestException(IpcError error) : base($"{error.Code}: {error.Message}") => Error = error;
}

/// <summary>Соединение со службой закрылось (например, до прихода ответа на запрос).</summary>
public sealed class IpcConnectionClosedException : Exception
{
    public IpcConnectionClosedException() : base("Соединение со службой закрыто.") { }
}
