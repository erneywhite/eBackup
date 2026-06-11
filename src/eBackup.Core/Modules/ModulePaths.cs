namespace eBackup.Core.Modules;

/// <summary>Расположения, связанные с модулями.</summary>
public static class ModulePaths
{
    /// <summary>Папка пользовательских модулей (drop-in/импорт), без прав админа.</summary>
    public static string ModulesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eBackup", "modules");

    /// <summary>Список id выключенных модулей (выключен = не участвует в бэкапах).</summary>
    public static string DisabledModulesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eBackup", "disabled-modules.json");
}
