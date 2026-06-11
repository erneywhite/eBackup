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

    /// <summary>Только буквы/цифры/дефис: имя машины попадает в имя файла.</summary>
    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray())
            .Trim('-');
        return cleaned.Length == 0 ? "pc" : cleaned;
    }
}
