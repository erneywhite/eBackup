using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace eBackup.Modules.VTubeStudio;

/// <summary>
/// Поиск установленных Steam-игр БЕЗ зависимости от вошедшего пользователя (служба под SYSTEM):
/// корень Steam берём из HKLM (у SYSTEM нет HKCU\Valve\Steam), список библиотек — из
/// steamapps/libraryfolders.vdf. Игры на разных дисках (C:\Program Files (x86)\Steam, D:\SteamLibrary…)
/// токеном путей не выразить — отсюда отдельный дискавери (переиспользуемо для будущих Steam-модулей).
/// </summary>
public static class SteamLibraries
{
    /// <summary>Корень установки Steam из HKLM (machine-wide), или null если Steam не найден.</summary>
    public static string? FindSteamRoot()
    {
        foreach (var sub in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default).OpenSubKey(sub);
                if (key?.GetValue("InstallPath") is string p && Directory.Exists(p))
                    return p;
            }
            catch { /* нет доступа/ключа — пробуем следующий */ }
        }
        return null;
    }

    /// <summary>Все библиотечные папки Steam (сам корень + пути из libraryfolders.vdf).</summary>
    public static IReadOnlyList<string> LibraryPaths(string steamRoot)
    {
        var libs = new List<string> { steamRoot };
        try
        {
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                {
                    var path = m.Groups[1].Value.Replace("\\\\", "\\"); // в vdf слэши экранированы
                    if (Directory.Exists(path))
                        libs.Add(path);
                }
        }
        catch { /* битый/недоступный vdf — остаётся хотя бы корень */ }
        return libs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Папка установки игры по имени её common-папки в любой библиотеке (или null).</summary>
    public static string? FindAppCommonDir(string commonFolderName)
    {
        var root = FindSteamRoot();
        if (root is null)
            return null;
        foreach (var lib in LibraryPaths(root))
        {
            var dir = Path.Combine(lib, "steamapps", "common", commonFolderName);
            if (Directory.Exists(dir))
                return dir;
        }
        return null;
    }
}
