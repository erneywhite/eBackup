namespace eBackup.Core.Paths;

/// <summary>Защита от выхода за пределы целевой папки (zip-slip и т.п.).</summary>
public static class PathSafety
{
    /// <summary>true, если <paramref name="candidate"/> находится внутри <paramref name="root"/> (или равен ему) после канонизации.</summary>
    public static bool IsWithin(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
