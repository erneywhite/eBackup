using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using eBackup.Core.Engine;
using eBackup.Core.Modules;
using Xunit;

namespace eBackup.Tests;

public class BackupEngineResilienceTests
{
    /// <summary>Заблокированный/недоступный файл пропускается, а бэкап продолжается и собирается.</summary>
    [Fact]
    public async Task Locked_File_Is_Skipped_And_Backup_Continues()
    {
        var modsDir = Path.Combine(Path.GetTempPath(), $"ebk-mods-{Guid.NewGuid():N}");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var srcName = $"eBackup-locktest-{Guid.NewGuid():N}";
        var srcDir = Path.Combine(localAppData, srcName);
        var outDir = Path.Combine(Path.GetTempPath(), $"ebk-out-{Guid.NewGuid():N}");
        FileStream? locked = null;
        try
        {
            Directory.CreateDirectory(srcDir);
            await File.WriteAllTextAsync(Path.Combine(srcDir, "ok.txt"), "hello");
            var lockedPath = Path.Combine(srcDir, "locked.bin");
            await File.WriteAllTextAsync(lockedPath, "secret");
            // Эксклюзивный замок: CreateEntryFromFile не сможет открыть файл (sharing violation).
            locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

            Directory.CreateDirectory(modsDir);
            File.WriteAllText(Path.Combine(modsDir, "t.module.json"),
                $$"""{ "id": "t", "entries": [ { "tokenPath": "{LOCALAPPDATA}/{{srcName}}", "type": "Directory", "archivePath": "d" } ] }""");

            var module = new DeclarativeModuleSource(modsDir).Discover().Single().Instance!;
            var archive = await new BackupEngine().CreateBackupAsync([module], outDir, "lock");

            using var zip = ZipFile.OpenRead(archive);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("data/t/d/ok.txt", names);            // читаемый файл вошёл (под data/<id>/)
            Assert.DoesNotContain("data/t/d/locked.bin", names);  // заблокированный пропущен, бэкап не упал
        }
        finally
        {
            locked?.Dispose();
            foreach (var d in new[] { modsDir, srcDir, outDir })
                if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        }
    }
}
