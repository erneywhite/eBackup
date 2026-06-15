namespace eBackup.Ipc.Server;

/// <summary>
/// Кто вызывает (установлено из RunAsClient на S4). Все авторизационные решения и
/// per-OwnerSid резолв конфигов служба делает по этому SID, а не по токену продолжения.
/// </summary>
public sealed record CallerContext(string OwnerSid, bool IsAdmin)
{
    /// <summary>Заглушка для тестов диспетчера (живую личность ставит ClientIdentity на S4).</summary>
    public static readonly CallerContext Test = new("S-1-5-21-0-0-0-1000", IsAdmin: false);
}
