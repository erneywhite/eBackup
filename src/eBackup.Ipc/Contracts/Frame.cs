using System.Text.Json;

namespace eBackup.Ipc.Contracts;

/// <summary>Виды фреймов (на проводе — строки, чтобы неизвестный вид не ронял декод).</summary>
public static class FrameKinds
{
    public const string Req = "req";       // запрос (есть Id, Op)
    public const string Resp = "resp";     // ответ на запрос (есть Id)
    public const string Fault = "fault";   // ошибка по запросу (есть Id, Body=IpcError)
    public const string Note = "note";     // job-нотификация (нет Id; есть Op=тип, Seq)
    public const string Hello = "hello";   // рукопожатие
    public const string Ping = "ping";
    public const string Pong = "pong";
}

/// <summary>
/// Конверт фрейма — ЗАМОРОЖЕН/append-only навсегда: любая версия должна уметь его распарсить
/// до того, как дойдёт до Hello. Тело (<see cref="Body"/>) остаётся сырым <see cref="JsonElement"/>
/// и десериализуется ТОЛЬКО когда op/kind распознан (двухстадийный декод → forward-compat).
/// </summary>
public sealed record Frame
{
    /// <summary>Версия конверта (= WireProtocol на момент отправки).</summary>
    public int V { get; init; } = IpcContractInfo.WireProtocol;

    /// <summary>Вид фрейма (см. <see cref="FrameKinds"/>) — строкой ради forward-compat.</summary>
    public string Kind { get; init; } = "";

    /// <summary>Корреляционный id (req/resp/fault). Для note — null.</summary>
    public string? Id { get; init; }

    /// <summary>Имя операции (req) или тип нотификации (note).</summary>
    public string? Op { get; init; }

    /// <summary>Порядковый номер в рамках задачи (для note/replay).</summary>
    public long? Seq { get; init; }

    /// <summary>Тело — сырой JSON, декодируется по месту после распознавания op. null — тела нет (Ping/Pong и т.п.).</summary>
    public JsonElement? Body { get; init; }
}
