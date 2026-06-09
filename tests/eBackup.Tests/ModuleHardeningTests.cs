using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Core.Model;
using eBackup.Core.Modules;
using Xunit;

namespace eBackup.Tests;

public class ModuleHardeningTests
{
    [Theory]
    [InlineData("obs", true)]
    [InlineData("my.app_1-2", true)]
    [InlineData("../..", false)]
    [InlineData("UPPER", false)]
    [InlineData("", false)]
    public void IsValidId_Works(string id, bool expected)
        => Assert.Equal(expected, ModuleValidation.IsValidId(id));

    [Theory]
    [InlineData("a/b", true)]
    [InlineData("../x", false)]
    [InlineData("/x", false)]
    [InlineData("C:/x", false)]
    public void IsSafeArchivePath_Works(string p, bool expected)
        => Assert.Equal(expected, ModuleValidation.IsSafeArchivePath(p));

    private sealed class ThrowingModule : IBackupModule
    {
        public string Id => "boom";
        public string DisplayName => "Boom";
        public Task<IReadOnlyList<PathEntry>> DiscoverAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("kaboom");
    }

    private sealed class DirMod(string token) : IBackupModule
    {
        public string Id => "good";
        public string DisplayName => "Good";
        public Task<IReadOnlyList<PathEntry>> DiscoverAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PathEntry>>(
            [
                new PathEntry { TokenPath = token, Type = PathEntryType.Directory, ArchivePath = "good/data" }
            ]);
    }

    [Fact]
    public async Task Throwing_Module_Is_Isolated_And_Backup_Still_Succeeds()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var srcName = $"eBackup-iso-{Guid.NewGuid():N}";
        var srcDir = Path.Combine(localAppData, srcName);
        var outDir = Path.Combine(Path.GetTempPath(), $"ebk-out-{Guid.NewGuid():N}");
        var restoreDir = Path.Combine(Path.GetTempPath(), $"ebk-restore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(srcDir);
            await File.WriteAllTextAsync(Path.Combine(srcDir, "keep.txt"), "k");

            var engine = new BackupEngine();
            var archive = await engine.CreateBackupAsync(
                [new ThrowingModule(), new DirMod("{LOCALAPPDATA}/" + srcName)], outDir, "iso");

            await engine.RestoreAsync(archive, destinationRootOverride: restoreDir);

            // Упавший модуль пропущен, рабочий — сохранён и восстановлен.
            Assert.True(File.Exists(Path.Combine(restoreDir, "good", "data", "keep.txt")));
        }
        finally
        {
            foreach (var d in new[] { srcDir, outDir, restoreDir })
                if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_Rejects_ZipSlip_ArchivePath_In_Manifest()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"ebk-evil-{Guid.NewGuid():N}.ebk");
        var restoreDir = Path.Combine(Path.GetTempPath(), $"ebk-restore-{Guid.NewGuid():N}");
        try
        {
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("manifest.json");
                await using var s = entry.Open();
                var manifest = new Manifest
                {
                    CreatedAt = default,
                    Modules =
                    [
                        new ModuleEntry
                        {
                            ModuleId = "evil",
                            DisplayName = "evil",
                            Entries =
                            [
                                new PathEntry
                                {
                                    TokenPath = "{APPDATA}/x",
                                    Type = PathEntryType.File,
                                    ArchivePath = "../../escape.txt"
                                }
                            ]
                        }
                    ]
                };
                await JsonSerializer.SerializeAsync(s, manifest, ManifestJson.Options);
            }

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new BackupEngine().RestoreAsync(archivePath, destinationRootOverride: restoreDir));
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (Directory.Exists(restoreDir)) Directory.Delete(restoreDir, recursive: true);
        }
    }
}
