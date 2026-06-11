using System.Text.Json;

namespace eBackup.App;

/// <summary>
/// Настройки приложения (%APPDATA%/eBackup/settings.json). Загружаются свежими перед
/// каждым использованием, поэтому изменения применяются без перезапуска.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Куда складывать локальные архивы (и откуда их читает «Архивы»).</summary>
    public string LocalBackupDir { get; set; } = DefaultLocalBackupDir;

    /// <summary>Хранить последних N архивов; 0 — хранить все.</summary>
    public int RetentionCount { get; set; }

    /// <summary>Закрытие окна сворачивает в трей (расписания продолжают работать).</summary>
    public bool MinimizeToTray { get; set; } = true;

    public static string DefaultLocalBackupDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "eBackup", "Backups");

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eBackup", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath)) ?? new AppSettings();
        }
        catch
        {
            // повреждённый файл настроек — работаем с умолчаниями
        }
        return new AppSettings();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, ConfigPath, overwrite: true);
    }
}
