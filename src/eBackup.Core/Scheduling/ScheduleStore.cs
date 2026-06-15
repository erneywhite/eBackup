using System.Text.Json;
using eBackup.Core.Model;
using eBackup.Core.Security;
using eBackup.Platform;

namespace eBackup.Core.Scheduling;

/// <summary>
/// Хранилище расписаний (%APPDATA%/eBackup/schedules.json). Парольные фразы шифруются
/// переданным <see cref="ISecretProtector"/> (DPAPI) — открытым текстом не лежат.
/// </summary>
public sealed class ScheduleStore(ISecretProtector protector, string? filePath = null)
{
    private readonly string _filePath = filePath ?? DefaultFilePath();

    public static string DefaultFilePath() => AppPaths.SchedulesFile;

    public async Task<IReadOnlyList<BackupSchedule>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return [];

        await using var stream = File.OpenRead(_filePath);
        var list = await JsonSerializer
            .DeserializeAsync<List<BackupSchedule>>(stream, ManifestJson.Options, ct)
            .ConfigureAwait(false);
        return list ?? [];
    }

    public async Task SaveAllAsync(IEnumerable<BackupSchedule> schedules, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = _filePath + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer
                .SerializeAsync(stream, schedules.ToList(), ManifestJson.Options, ct)
                .ConfigureAwait(false);
        }
        File.Move(tmp, _filePath, overwrite: true);
    }

    public string ProtectPassphrase(string passphrase) => protector.Protect(passphrase);
    public string UnprotectPassphrase(string protectedValue) => protector.Unprotect(protectedValue);
}
