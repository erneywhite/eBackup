namespace eBackup.Security;

/// <summary>Базовая ошибка машинного ключа.</summary>
public class MachineKeyException : Exception
{
    public MachineKeyException(string message) : base(message) { }
    public MachineKeyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Файл ключа физически повреждён (не сошлась контрольная сумма / структура).</summary>
public sealed class MachineKeyCorruptedException : MachineKeyException
{
    public MachineKeyCorruptedException(string message) : base(message) { }
}

/// <summary>Файл ключа цел, но DPAPI LocalMachine не расшифровал его — ключ машины изменился.</summary>
public sealed class MachineKeyChangedException : MachineKeyException
{
    public MachineKeyChangedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Запрошенный keyId на этой машине недоступен (секрет создан другой установкой/машиной).</summary>
public sealed class MachineKeyUnavailableException : MachineKeyException
{
    public MachineKeyUnavailableException(string message) : base(message) { }
}
