using System.Text.Json;
using eBackup.Platform;

namespace eBackup.App;

/// <summary>Общий список «своих папок» (страница «Бэкап» редактирует, расписания читают).</summary>
public static class CustomFolderConfig
{
    private static string ConfigPath => AppPaths.CustomFoldersFile;

    public static List<string> Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ConfigPath)) ?? [];
        }
        catch
        {
            // повреждённый конфиг — начнём с пустого списка
        }
        return [];
    }

    public static void Save(List<string> folders)
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(folders));
        }
        catch
        {
            // несохранённый список не критичен для текущего запуска
        }
    }
}
