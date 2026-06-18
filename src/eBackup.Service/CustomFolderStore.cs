using System.Text.Json;
using eBackup.Platform;

namespace eBackup.Service;

/// <summary>
/// Реестр «своих папок» в машинном конфиге (%ProgramData%\eBackup\config\custom-folders.json).
/// Id папки = её путь. Бэкап включает только ЗАРЕГИСТРИРОВАННЫЕ здесь папки — это граница: служба
/// под SYSTEM не читает произвольный путь с провода, только тот, что пользователь явно добавил.
/// </summary>
public sealed class CustomFolderStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly object _lock = new();

    public CustomFolderStore(string? file = null)
        => _file = file ?? Path.Combine(AppPaths.MachineConfigDir, "custom-folders.json");

    public IReadOnlyList<string> List()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(_file)
                    ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? []
                    : [];
            }
            catch { return []; }
        }
    }

    public void Upsert(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_lock)
        {
            var list = List().ToList();
            if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(path);
                Save(list);
            }
        }
    }

    public void Remove(string path)
    {
        lock (_lock)
        {
            var list = List().Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
            Save(list);
        }
    }

    public bool Contains(string path) => List().Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));

    private void Save(List<string> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, _file, overwrite: true);
    }
}
