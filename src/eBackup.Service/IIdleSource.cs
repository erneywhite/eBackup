namespace eBackup.Service;

/// <summary>
/// Источник «простоя» для планировщика: сколько времени система свободна (нет активного
/// разблокированного пользователя И система не под нагрузкой). Влияет только на режим
/// DailyWhenIdle — Daily/Weekly/EveryHours срабатывают независимо от простоя.
/// </summary>
public interface IIdleSource
{
    /// <summary>Текущая длительность простоя. Растёт, пока тихо; <see cref="TimeSpan.Zero"/> — система занята.</summary>
    Task<TimeSpan> GetIdleAsync(CancellationToken ct);
}
