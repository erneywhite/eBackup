namespace eBackup.Service;

/// <summary>
/// Источник «простоя» для планировщика: сколько времени система свободна с его точки зрения
/// (нет активного пользователя в сеансе И система не под нагрузкой). Влияет только на режим
/// DailyWhenIdle — Daily/Weekly/EveryHours срабатывают независимо. Настоящая реализация
/// (WTS-сеансы Session 0 + монитор нагрузки) приходит на S6-6.
/// </summary>
public interface IIdleSource
{
    /// <summary>Длительность простоя. <see cref="TimeSpan.MaxValue"/> — никто не залогинен (бэкапить свободно).</summary>
    TimeSpan GetIdle();
}

/// <summary>
/// Временная заглушка до S6-6: система считается ЗАНЯТОЙ (простоя нет). Режим «при простое» с ней
/// не срабатывает; бэкапы по времени (Daily/Weekly/EveryHours) работают уже сейчас.
/// </summary>
public sealed class NeverIdleSource : IIdleSource
{
    public TimeSpan GetIdle() => TimeSpan.Zero;
}
