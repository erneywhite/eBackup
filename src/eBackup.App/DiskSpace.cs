using System.Runtime.InteropServices;

namespace eBackup.App;

/// <summary>
/// Свободное место на диске. GetDiskFreeSpaceEx понимает и буквы дисков,
/// и UNC-пути (\\nas\share) — в отличие от DriveInfo.
/// </summary>
internal static class DiskSpace
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    /// <summary>false — место выяснить не удалось (это не повод отменять бэкап).</summary>
    public static bool TryGetFreeBytes(string path, out long freeBytes)
    {
        freeBytes = 0;
        try
        {
            // Целевая папка может ещё не существовать — поднимаемся до существующего предка.
            var probe = Path.GetFullPath(path);
            while (!Directory.Exists(probe))
            {
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent) || parent == probe)
                    break;
                probe = parent;
            }

            if (!GetDiskFreeSpaceExW(probe, out var free, out _, out _))
                return false;

            freeBytes = (long)Math.Min(free, long.MaxValue);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
