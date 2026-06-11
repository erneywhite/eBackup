using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace eBackup.App;

/// <summary>Снимок загрузки системы в процентах (null — метрика недоступна).</summary>
public sealed record SystemLoad(double Cpu, double Ram, double? Disk, double? Net)
{
    // Пороги «системе свободно»: ЦП/диск/сеть < 50%; ОЗУ — 85%, т.к. Windows
    // почти всегда держит память занятой кэшами и фоном (50% блокировал бы навсегда).
    public const double CpuMax = 50, RamMax = 85, DiskMax = 50, NetMax = 50;

    public bool IsCalm()
        => Cpu < CpuMax && Ram < RamMax
        && (Disk is null || Disk < DiskMax)
        && (Net is null || Net < NetMax);
}

/// <summary>
/// Замер загрузки ЦП/ОЗУ/диска/сети за короткое окно — чтобы «бэкап при простое»
/// не стартовал поверх тяжёлой компиляции/рендера/загрузки.
/// </summary>
public static class SystemLoadMonitor
{
    public static async Task<SystemLoad> SampleAsync(TimeSpan window)
    {
        GetSystemTimes(out var idle1, out var kernel1, out var user1);
        var (bytes1, speedBits) = SampleNetwork();

        PerformanceCounter? diskCounter = null;
        try
        {
            diskCounter = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total");
            diskCounter.NextValue(); // первый вызов всегда 0 — прогрев
        }
        catch
        {
            diskCounter?.Dispose();
            diskCounter = null; // счётчики могут быть сломаны/недоступны — метрику пропустим
        }

        await Task.Delay(window);

        GetSystemTimes(out var idle2, out var kernel2, out var user2);
        var (bytes2, _) = SampleNetwork();

        // ЦП: kernel-время включает idle, поэтому total = kernel + user.
        var idleDelta = ToUlong(idle2) - ToUlong(idle1);
        var totalDelta = (ToUlong(kernel2) - ToUlong(kernel1)) + (ToUlong(user2) - ToUlong(user1));
        var cpu = totalDelta == 0 ? 0 : Math.Clamp(100.0 * (1.0 - (double)idleDelta / totalDelta), 0, 100);

        // ОЗУ: dwMemoryLoad — процент занятой физической памяти.
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        var ram = GlobalMemoryStatusEx(ref mem) ? mem.dwMemoryLoad : 0;

        double? disk = null;
        if (diskCounter is not null)
        {
            try
            {
                disk = Math.Clamp(100.0 - diskCounter.NextValue(), 0, 100);
            }
            catch
            {
                // метрика недоступна
            }
            finally
            {
                diskCounter.Dispose();
            }
        }

        double? net = null;
        if (speedBits > 0)
        {
            var bits = Math.Max(0, bytes2 - bytes1) * 8.0 / window.TotalSeconds;
            net = Math.Clamp(100.0 * bits / speedBits, 0, 100);
        }

        return new SystemLoad(cpu, ram, disk, net);
    }

    /// <summary>Суммарный трафик активных адаптеров + их суммарная скорость (бит/с).</summary>
    private static (long Bytes, long SpeedBits) SampleNetwork()
    {
        long bytes = 0, speed = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                    nic.Speed <= 0)
                    continue;

                var stats = nic.GetIPv4Statistics();
                bytes += stats.BytesReceived + stats.BytesSent;
                speed += nic.Speed;
            }
        }
        catch
        {
            return (0, 0);
        }
        return (bytes, speed);
    }

    private static ulong ToUlong(FILETIME t)
        => ((ulong)(uint)t.dwHighDateTime << 32) | (uint)t.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
