using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace eBackup.Service;

/// <summary>
/// Источник простоя для службы в Session 0. GetLastInputInfo в сеансе 0 не работает, поэтому
/// «занятость» определяем по WTS-сеансам: есть АКТИВНЫЙ РАЗБЛОКИРОВАННЫЙ пользователь → занято.
/// Дополнительно AND-гейт по загрузке (CPU/диск/сеть/ОЗУ): тяжёлая фоновая работа = не простой.
/// Длительность простоя накапливается между тиками (растёт, пока тихо; сбрасывается при активности).
/// Неопределённость трактуем консервативно как занятость (лучше не запустить, чем поверх работы).
/// </summary>
public sealed class SessionIdleSource : IIdleSource
{
    private static readonly TimeSpan LoadWindow = TimeSpan.FromSeconds(1);
    private readonly ILogger<SessionIdleSource> _log;
    private DateTime _idleSince = DateTime.Now; // момент, когда система стала «тихой»

    public SessionIdleSource(ILogger<SessionIdleSource> log) => _log = log;

    public async Task<TimeSpan> GetIdleAsync(CancellationToken ct)
    {
        var now = DateTime.Now;

        // Активный разблокированный пользователь за машиной — занято, сбрасываем счётчик.
        if (AnyActiveUnlockedSession())
        {
            _idleSince = now;
            return TimeSpan.Zero;
        }

        // Никто не работает интерактивно, но проверим, не идёт ли тяжёлая фоновая работа.
        SystemLoad load;
        try { load = await SystemLoadMonitor.SampleAsync(LoadWindow).ConfigureAwait(false); }
        catch { _idleSince = now; return TimeSpan.Zero; } // нет метрик → осторожно считаем занятым

        if (!load.IsCalm())
        {
            _idleSince = now;
            return TimeSpan.Zero;
        }

        return now - _idleSince; // тихо: простой растёт
    }

    /// <summary>Есть ли активный пользовательский сеанс, который НЕ заблокирован (т.е. кто-то работает).</summary>
    private bool AnyActiveUnlockedSession()
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var ptr, out var count))
        {
            _log.LogDebug("WTSEnumerateSessions недоступна — считаю систему занятой.");
            return true; // не смогли определить → консервативно «занято»
        }
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(ptr + i * size);
                if (info.State != WTS_CONNECTSTATE_CLASS.WTSActive || info.SessionId == 0)
                    continue; // не активен или сам сеанс служб — не «за машиной»

                // Активен. Считаем «работает», пока НЕ подтверждено, что сеанс заблокирован.
                if (IsLocked(info.SessionId) != true)
                    return true;
            }
            return false;
        }
        finally { WTSFreeMemory(ptr); }
    }

    /// <summary>true — заблокирован; false — разблокирован; null — определить не удалось.</summary>
    private static bool? IsLocked(int sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTS_INFO_CLASS.WTSSessionInfoEx, out var buf, out var bytes)
            || buf == IntPtr.Zero || bytes < Marshal.SizeOf<WTSINFOEX_TRUNC>())
            return null;
        try
        {
            var ex = Marshal.PtrToStructure<WTSINFOEX_TRUNC>(buf);
            if (ex.Level != 1) return null;
            // Win8+: WTS_SESSIONSTATE_LOCK = 0, WTS_SESSIONSTATE_UNLOCK = 1.
            // (На Win7/2008 R2 значения инвертированы — там вернём null, что трактуется как «занято».)
            return ex.SessionFlags switch
            {
                0 => true,
                1 => false,
                _ => null,
            };
        }
        finally { WTSFreeMemory(buf); }
    }

    // ---- WTS interop ----

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive, WTSConnected, WTSConnectQuery, WTSShadow, WTSDisconnected,
        WTSIdle, WTSListen, WTSReset, WTSDown, WTSInit
    }

    private enum WTS_INFO_CLASS { WTSSessionInfoEx = 25 }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    // Усечённый WTSINFOEX: читаем только Level + начало LEVEL1 (SessionId/SessionState/SessionFlags).
    // PtrToStructure читает ровно столько байт — нативный буфер больше, остаток нам не нужен.
    [StructLayout(LayoutKind.Sequential)]
    private struct WTSINFOEX_TRUNC
    {
        public int Level;
        public int SessionId;
        public WTS_CONNECTSTATE_CLASS SessionState;
        public int SessionFlags;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version,
        out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId,
        WTS_INFO_CLASS infoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
