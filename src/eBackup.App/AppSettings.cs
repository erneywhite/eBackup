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

    /// <summary>Дефолтное хранилище «Локальная папка» уже создано (одноразовая инициализация).</summary>
    public bool DefaultStorageCreated { get; set; }

    /// <summary>Добавлять имя компьютера в имя архива (несколько ПК в одном хранилище).</summary>
    public bool IncludeMachineNameInArchive { get; set; }

    /// <summary>Сжатие архива: 0 — быстрее, 1 — обычное, 2 — максимальное.</summary>
    public int CompressionMode { get; set; } = 1;

    /// <summary>Папка ассетов при восстановлении; null/пусто — Documents\eBackup\Assets.</summary>
    public string? DefaultAssetsDir { get; set; }

    /// <summary>Автопроверка хранилищ каждые N минут; 0 — только вручную (кнопка ⟳).</summary>
    public int SelfTestMinutes { get; set; } = 5;

    /// <summary>CompressionMode → уровень сжатия ZIP.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public System.IO.Compression.CompressionLevel CompressionLevel => CompressionMode switch
    {
        0 => System.IO.Compression.CompressionLevel.Fastest,
        2 => System.IO.Compression.CompressionLevel.SmallestSize,
        _ => System.IO.Compression.CompressionLevel.Optimal
    };

    public static string DefaultAssetsDirPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "eBackup", "Assets");

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
