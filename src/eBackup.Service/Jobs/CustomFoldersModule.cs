using System.Text;
using eBackup.Core.Abstractions;
using eBackup.Core.Model;

namespace eBackup.Service.Jobs;

/// <summary>
/// Синтетический модуль из «своих папок» задачи: каждая папка → запись-директория. Пути обычно
/// абсолютные (служба под SYSTEM их читает напрямую); токены ({USERPROFILE}/…) развернёт per-SID
/// резолвер движка. ArchivePath строится из имени папки, уникализируется и санитизируется.
/// </summary>
public sealed class CustomFoldersModule : IBackupModule
{
    private readonly IReadOnlyList<string> _folders;

    public CustomFoldersModule(IReadOnlyList<string> folders) => _folders = folders;

    public string Id => "folders";
    public string DisplayName => "Свои папки";

    public Task<IReadOnlyList<PathEntry>> DiscoverAsync(CancellationToken ct = default)
    {
        var entries = new List<PathEntry>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var key = Unique(SanitizeLeaf(folder), used);
            entries.Add(new PathEntry
            {
                TokenPath = folder,
                Type = PathEntryType.Directory,
                ArchivePath = "folders/" + key,
            });
        }
        return Task.FromResult<IReadOnlyList<PathEntry>>(entries);
    }

    private static string SanitizeLeaf(string folder)
    {
        var leaf = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(leaf)) leaf = "root"; // напр. «C:\» — корень диска
        var sb = new StringBuilder(leaf.Length);
        foreach (var ch in leaf)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or ' ' ? ch : '_');
        return sb.ToString();
    }

    private static string Unique(string key, HashSet<string> used)
    {
        var candidate = key;
        var n = 2;
        while (!used.Add(candidate))
            candidate = $"{key}-{n++}";
        return candidate;
    }
}
