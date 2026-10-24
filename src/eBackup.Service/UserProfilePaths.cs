using eBackup.Core.Paths;
using Microsoft.Win32;

namespace eBackup.Service;

/// <summary>
/// Разворачивает токены путей ({APPDATA} и т.п.) в профиль КОНКРЕТНОГО пользователя по его SID —
/// нужно службе под SYSTEM, иначе {APPDATA} указывал бы в системный профиль. Профиль берётся из
/// реестра (ProfileList\&lt;SID&gt;\ProfileImagePath). Машинные токены ({PROGRAMDATA}/{PROGRAMFILES})
/// одинаковы для всех. Неизвестный SID → откат на <see cref="PathTokens.Resolve"/> (профиль процесса).
/// </summary>
public sealed class UserProfilePaths
{
    private readonly string? _profile; // корень профиля пользователя, напр. C:\Users\erney

    public UserProfilePaths(string sid) => _profile = ReadProfileImagePath(sid);

    /// <summary>Корень профиля пользователя по SID (ProfileList); null — SID не сопоставлен.</summary>
    public static string? ProfileRoot(string sid) => ReadProfileImagePath(sid);

    /// <summary>
    /// Профили ВСЕХ остальных учёток из ProfileList (включая systemprofile и служебные) — для отката
    /// проверки прав: чужие профили читать нельзя. Берём из реестра, а не «соседей» по C:\Users,
    /// потому что профиль может быть перенесён на другой диск.
    /// </summary>
    public static IReadOnlyList<string> OtherProfileRoots(string sid)
    {
        var result = new List<string>();
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (list is null) return result;
            foreach (var name in list.GetSubKeyNames())
            {
                if (string.Equals(name, sid, StringComparison.OrdinalIgnoreCase)) continue;
                using var key = list.OpenSubKey(name);
                if (key?.GetValue("ProfileImagePath") is string path && path.Length > 0)
                    result.Add(path);
            }
        }
        catch
        {
            // нет доступа к реестру — пустой список; вызывающий добавляет свои запреты сверху
        }
        return result;
    }

    public string Resolve(string tokenPath)
    {
        if (_profile is null) return PathTokens.Resolve(tokenPath); // SID не сопоставлен — как раньше

        string? baseDir = null, rest = null;
        if (TryToken(tokenPath, "{USERPROFILE}", out rest)) baseDir = _profile;
        else if (TryToken(tokenPath, "{APPDATA}", out rest)) baseDir = Path.Combine(_profile, "AppData", "Roaming");
        else if (TryToken(tokenPath, "{LOCALAPPDATA}", out rest)) baseDir = Path.Combine(_profile, "AppData", "Local");
        else if (TryToken(tokenPath, "{PROGRAMDATA}", out rest))
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        else if (TryToken(tokenPath, "{PROGRAMFILES}", out rest))
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (baseDir is null) // нет известного токена — сырой путь как есть
            return tokenPath.Replace('/', Path.DirectorySeparatorChar);

        return Path.Combine(baseDir, rest!.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool TryToken(string tokenPath, string token, out string rest)
    {
        if (tokenPath.StartsWith(token, StringComparison.Ordinal))
        {
            rest = tokenPath[token.Length..].TrimStart('/', '\\');
            return true;
        }
        rest = string.Empty;
        return false;
    }

    private static string? ReadProfileImagePath(string sid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            return key?.GetValue("ProfileImagePath") as string;
        }
        catch
        {
            return null;
        }
    }
}
