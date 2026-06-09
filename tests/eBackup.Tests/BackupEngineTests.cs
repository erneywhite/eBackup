using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Core.Model;
using Xunit;

namespace eBackup.Tests;

public class BackupEngineTests
{
    /// <summary>Тестовый модуль: бэкапит указанный токенизированный каталог.</summary>
    private sealed class DirModule(string tokenPath) : IBackupModule
    {
        public string Id => "test";
        public string DisplayName => "Test";

        public Task<IReadOnlyList<PathEntry>> DiscoverAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PathEntry>>(
            [
                new PathEntry
                {
                    TokenPath = tokenPath,
                    Type = PathEntryType.Directory,
                    ArchivePath = "test/data"
                }
            ]);
    }

    [Fact]
    public async Task Backup_Then_Restore_To_Override_Roundtrips_Files()
    {
        // Источник кладём под реальный LOCALAPPDATA, чтобы токен резолвился штатно.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var srcName = $"eBackup-test-{Guid.NewGuid():N}";
        var srcDir = Path.Combine(localAppData, srcName);
        var outDir = Path.Combine(Path.GetTempPath(), $"ebk-out-{Guid.NewGuid():N}");
        var restoreDir = Path.Combine(Path.GetTempPath(), $"ebk-restore-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(srcDir);
            await File.WriteAllTextAsync(Path.Combine(srcDir, "hello.txt"), "привет");

            var engine = new BackupEngine();
            var module = new DirModule("{LOCALAPPDATA}/" + srcName);

            var archive = await engine.CreateBackupAsync([module], outDir, "rt");
            Assert.True(File.Exists(archive));

            // Восстановление в отдельную папку — не трогая реальные системные пути.
            await engine.RestoreAsync(archive, destinationRootOverride: restoreDir);

            var restored = Path.Combine(restoreDir, "test", "data", "hello.txt");
            Assert.True(File.Exists(restored));
            Assert.Equal("привет", await File.ReadAllTextAsync(restored));
        }
        finally
        {
            if (Directory.Exists(srcDir)) Directory.Delete(srcDir, recursive: true);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
            if (Directory.Exists(restoreDir)) Directory.Delete(restoreDir, recursive: true);
        }
    }
}
