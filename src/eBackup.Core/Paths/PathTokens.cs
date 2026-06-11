namespace eBackup.Core.Paths;

/// <summary>
/// Преобразует абсолютные Windows-пути в переносимые токены ({APPDATA} и т.п.) и обратно.
/// Это ключ к восстановлению на другой машине: в манифесте хранятся токены, а не жёсткие пути.
/// </summary>
public static class PathTokens
{
    // Порядок важен: более специфичные (длинные) пути идут первыми, чтобы
    // {LOCALAPPDATA} не схлопнулся в {APPDATA} и т.п.
    private static readonly (string Token, Environment.SpecialFolder Folder)[] Map =
    {
        ("{LOCALAPPDATA}", Environment.SpecialFolder.LocalApplicationData),
        ("{APPDATA}",      Environment.SpecialFolder.ApplicationData),
        ("{PROGRAMFILES}", Environment.SpecialFolder.ProgramFiles),
        ("{PROGRAMDATA}",  Environment.SpecialFolder.CommonApplicationData),
        ("{USERPROFILE}",  Environment.SpecialFolder.UserProfile),
    };

    /// <summary>Заменить известный префикс абсолютного пути на токен (с прямыми слэшами).</summary>
    public static string Tokenize(string absolutePath)
    {
        var normalized = absolutePath.Replace('\\', '/');
        foreach (var (token, folder) in Map)
        {
            var baseDir = Environment.GetFolderPath(folder).Replace('\\', '/');
            if (baseDir.Length > 0 &&
                normalized.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
            {
                return token + normalized[baseDir.Length..];
            }
        }
        return normalized;
    }

    /// <summary>Развернуть токен обратно в абсолютный путь на текущей машине.</summary>
    public static string Resolve(string tokenPath)
    {
        foreach (var (token, folder) in Map)
        {
            if (tokenPath.StartsWith(token, StringComparison.Ordinal))
            {
                var baseDir = Environment.GetFolderPath(folder);
                var rest = tokenPath[token.Length..].TrimStart('/', '\\');
                return Path.Combine(baseDir, rest.Replace('/', Path.DirectorySeparatorChar));
            }
        }
        return tokenPath.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Безопасно ли восстанавливать <paramref name="tokenPath"/> в исходное место:
    /// он ДОЛЖЕН начинаться с известного токена И после канонизации оставаться внутри
    /// каталога этого токена (никаких сырых абсолютных путей и побегов через «..»).
    /// Граница доверия — манифест архива: путь оттуда не должен указывать «куда угодно».
    /// </summary>
    public static bool ResolvesWithinTokenRoot(string tokenPath, out string resolved)
    {
        foreach (var (token, folder) in Map)
        {
            if (!tokenPath.StartsWith(token, StringComparison.Ordinal))
                continue;

            var baseDir = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(baseDir))
                break;

            var rest = tokenPath[token.Length..].TrimStart('/', '\\');
            resolved = Path.Combine(baseDir, rest.Replace('/', Path.DirectorySeparatorChar));

            var fullBase = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar);
            var fullTarget = Path.GetFullPath(resolved);
            return fullTarget.Equals(fullBase, StringComparison.OrdinalIgnoreCase)
                || fullTarget.StartsWith(fullBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        resolved = string.Empty;
        return false;
    }
}
