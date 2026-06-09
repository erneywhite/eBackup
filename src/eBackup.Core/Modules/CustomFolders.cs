using eBackup.Core.Model;
using eBackup.Core.Paths;

namespace eBackup.Core.Modules;

/// <summary>
/// «Свои папки» пользователя как обычный декларативный модуль (id "folders").
/// Имена папок сохраняются как есть (включая кириллицу) — индекс добавляется
/// только при совпадении имён; пути токенизируются для переносимости архива.
/// </summary>
public static class CustomFolders
{
    public static DeclarativeModule? Build(IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
            return null;

        var entries = new List<PathEntry>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            var leaf = Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(leaf))
                leaf = "диск-" + char.ToUpperInvariant(folder[0]); // корень диска, напр. "S:\"
            leaf = leaf.Replace('/', '-').Replace('\\', '-').Trim();
            while (leaf.Contains(".."))
                leaf = leaf.Replace("..", "."); // ".." заблокирован валидацией манифеста

            var name = leaf;
            var n = 2;
            while (!used.Add(name))
                name = $"{leaf} ({n++})";

            entries.Add(new PathEntry
            {
                TokenPath = PathTokens.Tokenize(folder),
                Type = PathEntryType.Directory,
                ArchivePath = $"folders/{name}"
            });
        }

        return new DeclarativeModule("folders", "Свои папки", entries);
    }
}
