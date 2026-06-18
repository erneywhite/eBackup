using System.Text.Json;
using eBackup.Platform;

namespace eBackup.Core.History;

/// <summary>
/// Журнал запусков бэкапа: runs.json с последними записями + полный текстовый
/// лог каждого запуска с таймкодами в logs/&lt;id&gt;.log. Журнал — вспомогательная
/// вещь: любая ошибка чтения/записи здесь не должна мешать самому бэкапу,
/// поэтому всё проглатывается тихо.
/// </summary>
public sealed class HistoryStore
{
    public const int MaxRuns = 300;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _logLock = new(); // лог пишут и UI-поток, и рабочий поток движка
    private readonly string _dir;

    public HistoryStore(string? directory = null) => _dir = directory ?? DefaultDirectory;

    public static string DefaultDirectory => AppPaths.HistoryDir;

    private string IndexPath => Path.Combine(_dir, "runs.json");

    public string LogPath(string runId) => Path.Combine(_dir, "logs", runId + ".log");

    /// <summary>Записи журнала, новые сверху. Битый файл — пустой список, не краш.</summary>
    public async Task<List<BackupRunRecord>> LoadAsync()
    {
        try
        {
            if (!File.Exists(IndexPath))
                return [];
            await using var stream = File.OpenRead(IndexPath);
            var list = await JsonSerializer.DeserializeAsync<List<BackupRunRecord>>(stream) ?? [];
            return list.OrderByDescending(r => r.StartedAt).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Добавить запись или обновить существующую (по Id). Старые — за кадр, вместе с логами.</summary>
    public async Task SaveRunAsync(BackupRunRecord run)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var list = await LoadAsync();
            list.RemoveAll(r => r.Id == run.Id);
            list.Insert(0, run);

            foreach (var removed in list.Skip(MaxRuns))
                try { File.Delete(LogPath(removed.Id)); } catch { }
            list = list.Take(MaxRuns).ToList();

            var tmp = IndexPath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(list, Json));
            File.Move(tmp, IndexPath, overwrite: true);
        }
        catch
        {
            // журнал не должен ломать бэкап
        }
    }

    /// <summary>Строка в лог запуска, с таймкодом до миллисекунд.</summary>
    public void AppendLog(string runId, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (_logLock)
            {
                var path = LogPath(runId);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
        }
    }

    public string ReadLog(string runId)
    {
        try
        {
            return File.Exists(LogPath(runId)) ? File.ReadAllText(LogPath(runId)) : string.Empty;
        }
        catch (Exception ex)
        {
            return "не удалось прочитать лог: " + ex.Message;
        }
    }

    /// <summary>
    /// Постранично прочитать строки лога ПОСЛЕ <paramref name="fromSeq"/> (seq = номер строки, 1-based),
    /// не более <paramref name="maxLines"/>. Журнал append-only, поэтому номер строки — стабильный seq.
    /// Нужно службе 1.2 для seq-адресной выдачи прогресса по IPC (GetRunLog) и догона пропусков.
    /// Формат строк на диске не меняется — это read-only поверх существующего лога.
    /// </summary>
    public IReadOnlyList<(long Seq, string Text)> ReadLogFromSeq(string runId, long fromSeq, int maxLines)
    {
        var result = new List<(long, string)>();
        if (maxLines <= 0) return result;
        try
        {
            var path = LogPath(runId);
            lock (_logLock) // не гоняться с писателем (AppendLog)
            {
                if (!File.Exists(path)) return result;
                var all = File.ReadAllLines(path);
                var start = fromSeq <= 0 ? 0 : (fromSeq >= all.Length ? all.Length : (int)fromSeq);
                for (var i = start; i < all.Length && result.Count < maxLines; i++)
                    result.Add((i + 1, all[i])); // seq = номер строки (1-based)
            }
        }
        catch
        {
            // журнал — вспомогательный, чтение не должно ничего ронять
        }
        return result;
    }
}
