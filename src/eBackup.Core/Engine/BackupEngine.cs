using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using eBackup.Core.Abstractions;
using eBackup.Core.Model;
using eBackup.Core.Paths;

namespace eBackup.Core.Engine;

/// <summary>
/// Собирает архивы .ebk из модулей и восстанавливает их обратно по манифесту.
///
/// Формат v1: ZIP-контейнер — manifest.json в корне + data/&lt;module&gt;/... .
/// Опциональное шифрование (AES-256-GCM поверх готового ZIP) — TODO следующего этапа.
/// </summary>
public sealed class BackupEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Создать архив из набора модулей. Возвращает путь к готовому .ebk.
    /// </summary>
    public async Task<string> CreateBackupAsync(
        IEnumerable<IBackupModule> modules,
        string outputDirectory,
        string archiveName,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var archivePath = Path.Combine(outputDirectory, archiveName + ".ebk");

        var manifest = new Manifest
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Source = new SourceMachine
            {
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                OsVersion = Environment.OSVersion.VersionString
            }
        };

        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            foreach (var module in modules)
            {
                ct.ThrowIfCancellationRequested();
                var entries = await module.DiscoverAsync(ct).ConfigureAwait(false);
                var moduleEntry = new ModuleEntry
                {
                    ModuleId = module.Id,
                    DisplayName = module.DisplayName
                };

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var source = PathTokens.Resolve(entry.TokenPath);

                    if (entry.Type == PathEntryType.File && File.Exists(source))
                    {
                        var archiveEntryPath = "data/" + entry.ArchivePath.Replace('\\', '/');
                        zip.CreateEntryFromFile(source, archiveEntryPath);
                        moduleEntry.Entries.Add(entry with { Sha256 = await Sha256OfFileAsync(source, ct).ConfigureAwait(false) });
                    }
                    else if (entry.Type == PathEntryType.Directory && Directory.Exists(source))
                    {
                        var basePrefix = "data/" + entry.ArchivePath.Replace('\\', '/');
                        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                        {
                            ct.ThrowIfCancellationRequested();
                            var rel = Path.GetRelativePath(source, file).Replace('\\', '/');
                            zip.CreateEntryFromFile(file, basePrefix + "/" + rel);
                        }
                        moduleEntry.Entries.Add(entry);
                    }
                    // TODO(v1+): RegistryKey — экспорт/импорт ветки реестра.
                }

                manifest.Modules.Add(moduleEntry);
            }

            // Манифест в корень архива.
            var manifestEntry = zip.CreateEntry("manifest.json");
            using var ms = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(ms, manifest, JsonOptions, ct).ConfigureAwait(false);
        }

        return archivePath;
    }

    /// <summary>
    /// Распаковать архив .ebk и разложить файлы по местам согласно манифесту.
    /// </summary>
    public async Task RestoreAsync(
        string archivePath,
        ConflictPolicy conflictPolicy = ConflictPolicy.BackupExisting,
        CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidDataException("В архиве нет manifest.json — это не архив eBackup.");

        Manifest manifest;
        using (var ms = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<Manifest>(ms, JsonOptions, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("Не удалось прочитать manifest.json.");
        }

        foreach (var module in manifest.Modules)
        {
            foreach (var entry in module.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = PathTokens.Resolve(entry.TokenPath);
                var prefix = "data/" + entry.ArchivePath.Replace('\\', '/');

                if (entry.Type == PathEntryType.File)
                {
                    var ze = zip.GetEntry(prefix);
                    if (ze is not null)
                        ExtractFile(ze, target, conflictPolicy);
                }
                else if (entry.Type == PathEntryType.Directory)
                {
                    foreach (var ze in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(ze.Name)) continue; // пропустить маркеры папок
                        if (!ze.FullName.StartsWith(prefix + "/", StringComparison.Ordinal)) continue;

                        var rel = ze.FullName[(prefix.Length + 1)..];
                        var dest = Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar));
                        ExtractFile(ze, dest, conflictPolicy);
                    }
                }
                // TODO(v1+): RegistryKey — импорт ветки реестра.
            }
        }
    }

    private static void ExtractFile(ZipArchiveEntry entry, string destinationPath, ConflictPolicy policy)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(destinationPath))
        {
            switch (policy)
            {
                case ConflictPolicy.Skip:
                    return;
                case ConflictPolicy.BackupExisting:
                    File.Move(destinationPath, destinationPath + ".bak", overwrite: true);
                    break;
                case ConflictPolicy.Overwrite:
                default:
                    break;
            }
        }

        entry.ExtractToFile(destinationPath, overwrite: true);
    }

    private static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
