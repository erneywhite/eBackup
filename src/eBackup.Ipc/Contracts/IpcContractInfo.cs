namespace eBackup.Ipc.Contracts;

/// <summary>
/// Версионирование IPC-контракта GUI↔служба. Две оси: WireProtocol (только фрейминг/конверт)
/// и capabilities (авторитетный гейт фич, append-only). Дисциплина — как у ApiVersion плагинов.
/// </summary>
public static class IpcContractInfo
{
    /// <summary>Версия фрейминга/конверта. Бампается ТОЛЬКО при ломающем изменении транспорта.</summary>
    public const int WireProtocol = 1;

    /// <summary>Минимальный клиент, который сервер ещё обслуживает (лагает на ≥1 релиз от WireProtocol).</summary>
    public const int MinClientProtocol = 1;

    /// <summary>Минимальный сервер, с которым клиент ещё работает.</summary>
    public const int MinServerProtocol = 1;

    /// <summary>4-байтная преамбула перед первым фреймом — несовпадение формата конверта ловится сразу.</summary>
    public static readonly byte[] Preamble = "EBKI"u8.ToArray();

    /// <summary>Токены возможностей (append-only). Гейтят опциональные операции/поля.</summary>
    public static class Capabilities
    {
        /// <summary>Секреты переведены на машинный ключ — служба может их читать.</summary>
        public const string MachineKeySecrets = "machine-key-secrets";

        /// <summary>Расписания исполняет служба (S6) — GUI глушит свой DispatcherTimer.</summary>
        public const string SchedulingServiceOwned = "scheduling.serviceOwned";
    }
}
