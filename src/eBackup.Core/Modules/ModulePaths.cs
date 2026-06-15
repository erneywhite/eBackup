using eBackup.Platform;

namespace eBackup.Core.Modules;

/// <summary>Расположения, связанные с модулями.</summary>
public static class ModulePaths
{
    /// <summary>Папка пользовательских модулей (drop-in/импорт), без прав админа.</summary>
    public static string ModulesDirectory => AppPaths.ModulesDir;

    /// <summary>Список id выключенных модулей (выключен = не участвует в бэкапах).</summary>
    public static string DisabledModulesPath => AppPaths.DisabledModulesFile;
}
