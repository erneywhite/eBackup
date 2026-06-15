namespace eBackup.Ipc.Contracts;

/// <summary>
/// Ссылка на парольную фразу для бэкапа — НЕ открытый текст в самом запросе. «ticket» получают
/// заранее через StashPassphrase (разовый, привязан к соединению+SID). «none» — без шифрования.
/// Фраза расписания недоступна тут вовсе — её использует только RunScheduleNow (анти-confused-deputy).
/// </summary>
public sealed record PassphraseRef
{
    public string Kind { get; init; } = "none"; // none | ticket
    public string? Ticket { get; init; }

    public static PassphraseRef None { get; } = new();
    public static PassphraseRef FromTicket(string ticket) => new() { Kind = "ticket", Ticket = ticket };
}
