using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.Abstractions;
using eBackup.Core.Crypto;
using eBackup.Core.Engine;
using eBackup.Core.Model;
using Xunit;

namespace eBackup.Tests;

public class BackupEngineTests
{
    /// <summary>Тестовый модуль: бэкапит указанный токенизированный каталог.</summary>
    private sealed class DirModule(string tokenPath, string[]? excludes = null) : IBackupModule
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
                    ArchivePath = "test/data",
                    ExcludeGlobs = excludes ?? []
                }
            ]);
    }

    private sealed class ListProgress : IProgress<string>
    {
        public List<string> Items { get; } = [];
        public void Report(string value) => Items.Add(value);
    }

    [Fact]
    public async Task Backup_Reports_Progress_Messages()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var srcName = $"eBackup-test-{Guid.NewGuid():N}";
        var srcDir = Path.Combine(localAppData, srcName);
        var outDir = Path.Combine(Path.GetTempPath(), $"ebk-out-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(srcDir);
            await File.WriteAllTextAsync(Path.Combine(srcDir, "a.txt"), "x");

            var progress = new ListProgress();
            await new BackupEngine().CreateBackupAsync(
                [new DirModule("{LOCALAPPDATA}/" + srcName)], outDir, "pg", progress: progress);

            Assert.NotEmpty(progress.Items);
            Assert.Contains(progress.Items, m => m.Contains("манифест", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(srcDir)) Directory.Delete(srcDir, recursive: true);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
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

    [Fact]
    public async Task Backup_Skips_Files_Matching_ExcludeGlobs()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var srcName = $"eBackup-test-{Guid.NewGuid():N}";
        var srcDir = Path.Combine(localAppData, srcName);
        var outDir = Path.Combine(Path.GetTempPath(), $"ebk-out-{Guid.NewGuid():N}");
        var restoreDir = Path.Combine(Path.GetTempPath(), $"ebk-restore-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(srcDir, "logs"));
            await File.WriteAllTextAsync(Path.Combine(srcDir, "keep.txt"), "keep");
            await File.WriteAllTextAsync(Path.Combine(srcDir, "logs", "skip.txt"), "skip");

            var engine = new BackupEngine();
            var module = new DirModule("{LOCALAPPDATA}/" + srcName, ["logs/**"]);

            var archive = await engine.CreateBackupAsync([module], outDir, "ex");
            await engine.RestoreAsync(archive, destinationRootOverride: restoreDir);

            Assert.True(File.Exists(Path.Combine(restoreDir, "test", "data", "keep.txt")));
            Assert.False(File.Exists(Path.Combine(restoreDir, "test", "data", "logs", "skip.txt")));
        }
        finally
        {
            if (Directory.Exists(srcDir)) Directory.Delete(srcDir, recursive: true);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
            if (Directory.Exists(restoreDir)) Directory.Delete(restoreDir, recursive: true);
        }
    }

    [Fact]
    public async Task Encrypted_Backup_Restores_Only_With_Correct_Passphrase()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var srcName = $"eBackup-test-{Guid.NewGuid():N}";
        var srcDir = Path.Combine(localAppData, srcName);
        var outDir = Path.Combine(Path.GetTempPath(), $"ebk-out-{Guid.NewGuid():N}");
        var restoreDir = Path.Combine(Path.GetTempPath(), $"ebk-restore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(srcDir);
            await File.WriteAllTextAsync(Path.Combine(srcDir, "hello.txt"), "секрет");

            var engine = new BackupEngine();
            var module = new DirModule("{LOCALAPPDATA}/" + srcName);

            var archive = await engine.CreateBackupAsync([module], outDir, "enc", passphrase: "pw-12345");
            Assert.True(ArchiveCipher.IsEncrypted(archive));

            // Без фразы — отказ.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.RestoreAsync(archive, destinationRootOverride: restoreDir));

            // С верной фразой — успех.
            await engine.RestoreAsync(archive, destinationRootOverride: restoreDir, passphrase: "pw-12345");
            Assert.Equal("секрет",
                await File.ReadAllTextAsync(Path.Combine(restoreDir, "test", "data", "hello.txt")));
        }
        finally
        {
            foreach (var d in new[] { srcDir, outDir, restoreDir })
                if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        }
    }
}
