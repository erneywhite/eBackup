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
    public static string DefaultName(IEnumerable<IBackupModule> modules, DateTime? now = null)
    {
        var ids = modules.Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        var tag = ids.Count == 0 ? string.Empty : "_" + string.Join("-", ids);
        if (tag.Length > 32)
            tag = "_multi";

        return $"ebackup{tag}_{now ?? DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
    }
}
