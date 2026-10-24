using System.Text.RegularExpressions;
using eBackup.Core.Abstractions;

namespace eBackup.Core.Engine;

/// <summary>
/// Единая схема имён архивов для CLI и GUI:
/// «ebackup_&lt;id-модулей&gt;_&lt;дата&gt;_&lt;время&gt;», напр. «ebackup_obs_2026-06-10_14-05-33».
/// Идентификаторы модулей в имени помогают понять содержимое архива не открывая его;
/// дата в ISO-порядке читаема и корректно сортируется по имени файла.
/// </summary>
public static class BackupNaming
{
    /// <param name="machineTag">Имя компьютера в имени архива (полезно, когда несколько
    /// ПК бэкапятся в одно хранилище); null — без имени.</param>
    public static string DefaultName(
        IEnumerable<IBackupModule> modules, DateTime? now = null, string? machineTag = null)
    {
        var ids = modules.Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        var tag = ids.Count == 0 ? string.Empty : "_" + string.Join("-", ids);
        if (tag.Length > 32)
            tag = "_multi";

        var machine = string.IsNullOrWhiteSpace(machineTag) ? string.Empty : "_" + Sanitize(machineTag);

        return $"ebackup{machine}{tag}_{now ?? DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
    }

    private static readonly Regex TimestampSuffix =
        new(@"_(?<ts>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})(\.ebk)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Метка времени из хвоста имени («_2026-06-14_22-01-33[.ebk]»). Для retention она надёжнее
    /// LastWriteTime из листинга: FTP/WebDAV/облака нередко отдают время заливки или пустую дату.
    /// </summary>
    public static bool TryParseTimestamp(string archiveName, out DateTime timestamp)
    {
        timestamp = default;
        var m = TimestampSuffix.Match(archiveName);
        return m.Success && DateTime.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd_HH-mm-ss",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out timestamp);
    }

    /// <summary>
    /// Какие архивы удалить, чтобы в группе <paramref name="archiveName"/> осталось <paramref name="keep"/>
    /// последних. Порядок — по метке времени в имени (откат на <paramref name="lastWriteTime"/>, если
    /// метки нет); остальные группы не трогаются. keep ≤ 0 — ничего не удаляем.
    /// </summary>
    public static IReadOnlyList<T> SelectForRetention<T>(
        IEnumerable<T> files, Func<T, string> name, Func<T, DateTime> lastWriteTime, string archiveName, int keep)
    {
        if (keep <= 0) return [];
        var groupKey = RetentionGroupKey(archiveName);
        return files
            .Where(f => RetentionGroupKey(name(f)) == groupKey)
            .OrderByDescending(f => TryParseTimestamp(name(f), out var ts) ? ts : lastWriteTime(f))
            .ThenByDescending(lastWriteTime)
            .Skip(keep)
            .ToList();
    }

    /// <summary>
    /// Ключ группы для retention: имя архива без хвоста «_дата_время[.ebk]». Архивы одного
    /// набора модулей (ebackup_ПК_модули_…) дают один ключ — «хранить последние N» считается
    /// в пределах группы, чтобы бэкапы разных модулей не вытесняли друг друга.
    /// </summary>
    public static string RetentionGroupKey(string archiveName)
    {
        var stripped = TimestampSuffix.Replace(archiveName, "");
        return stripped.Length == 0 ? archiveName : stripped;
    }

    /// <summary>Только буквы/цифры/дефис: имя машины попадает в имя файла.</summary>
    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray())
            .Trim('-');
        return cleaned.Length == 0 ? "pc" : cleaned;
    }
}
