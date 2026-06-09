using System.Text.RegularExpressions;

namespace eBackup.Core.Modules;

/// <summary>
/// Общие проверки безопасности для модулей и манифеста (id, относительные пути).
/// Граница доверия — это ВВОД (дескриптор/архив), поэтому проверки применяются и при
/// загрузке дескрипторов, и при чтении манифеста на восстановлении.
/// </summary>
public static partial class ModuleValidation
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdRegex();

    public static bool IsValidId(string? id) => id is not null && IdRegex().IsMatch(id);

    /// <summary>Путь внутри архива безопасен: не пустой, без «..», не абсолютный, без ведущего слеша.</summary>
    public static bool IsSafeArchivePath(string? archivePath)
        => !string.IsNullOrEmpty(archivePath)
           && !archivePath.Contains("..")
           && !archivePath.StartsWith('/')
           && !archivePath.StartsWith('\\')
           && !Path.IsPathRooted(archivePath);
}
