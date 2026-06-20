namespace eBackup.Platform;

/// <summary>
/// Единая точка истины для расположений конфигов и данных eBackup.
/// До 1.2 пути были разбросаны по проектам — здесь сведены, чтобы служба
/// (LocalSystem) и GUI переключали раскладку в одном месте.
/// Поведение пока 1:1 со старыми путями (чистый рефактор S0).
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "eBackup";

    // Два корня (per-user). Позже служба сможет резолвить их по профилю SID.
    public static string Roaming => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string Local => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string Documents => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppFolderName);

    // Машинный (не per-user) корень — нужен службе под SYSTEM (1.2).
    public static string ProgramData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolderName);

    // --- Roaming (%APPDATA%\eBackup) ---
    public static string SettingsFile        => Path.Combine(Roaming, "settings.json");
    public static string CustomFoldersFile   => Path.Combine(Roaming, "custom-folders.json");
    public static string SchedulesFile       => Path.Combine(Roaming, "schedules.json");
    public static string ModulesDir          => Path.Combine(Roaming, "modules");
    public static string DisabledModulesFile => Path.Combine(Roaming, "disabled-modules.json");

    // --- Local (%LOCALAPPDATA%\eBackup) ---
    public static string HistoryDir            => Path.Combine(Local, "history");
    public static string StoragesFile          => Path.Combine(Local, "storages.json");
    public static string LegacyConnectionsFile => Path.Combine(Local, "connections.json");
    public static string CatalogCacheFile      => Path.Combine(Local, "catalog-cache.json");
    public static string LogsDir               => Path.Combine(Local, "logs");

    // --- Documents\eBackup (дефолтные дата-папки) ---
    public static string DefaultAssetsDir  => Path.Combine(Documents, "Assets");
    public static string DefaultBackupsDir => Path.Combine(Documents, "Backups");

    // --- Машинный ключ (%ProgramData%\eBackup\keys, 1.2) ---
    // Папка с born-locked DACL (SYSTEM+Администраторы) — создаётся привилегированно.
    public static string KeysDir        => Path.Combine(ProgramData, "keys");
    public static string MachineKeyFile => Path.Combine(KeysDir, "machine.key");

    // Машинный конфиг службы (SYSTEM-владелец, правится только через валидированный IPC, 1.2).
    public static string MachineConfigDir          => Path.Combine(ProgramData, "config");
    public static string MachineStoragesFile        => Path.Combine(MachineConfigDir, "storages.json");
    public static string MachineSchedulesFile       => Path.Combine(MachineConfigDir, "schedules.json");
    public static string MachineModulesDir          => Path.Combine(MachineConfigDir, "modules");
    public static string MachineDisabledModulesFile => Path.Combine(MachineConfigDir, "disabled-modules.json");

    /// <summary>Язык UI для строк службы/ядра (фазы, журнал, ошибки): "ru"/"en". Пишет служба по Hello от GUI.</summary>
    public static string MachineLanguageFile        => Path.Combine(MachineConfigDir, "ui-language");

    /// <summary>Папки, которые движок НИКОГДА не бэкапит (тул не должен архивировать свой ключ).</summary>
    public static IReadOnlyList<string> SecretBackupExclusions => [KeysDir];
}
