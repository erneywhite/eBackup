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
using eBackup.Core.Paths;
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

    [Theory]
    [InlineData("{APPDATA}/MyApp/config.ini", false)]       // легитимный путь — без «..»
    [InlineData("D:/Video/intro.mp4", false)]               // сырой абсолютный (своя папка) — допустим
    [InlineData("{APPDATA}/../../Windows/System32/x", true)] // побег через «..» — запрещён
    [InlineData("folders/../../etc/passwd", true)]           // побег через «..» — запрещён
    public void HasTraversal_Flags_Directory_Escapes(string tokenPath, bool expected)
        => Assert.Equal(expected, PathTokens.HasTraversal(tokenPath));

    [Fact]
    public async Task Selective_Restore_To_Original_Does_Not_Write_Managed_Entry()
    {
        // Враждебный архив: managed-ассет с сырым абсолютным TokenPath. При выборочном
        // восстановлении «в исходные места» движок ДОЛЖЕН пропустить его (ассеты кладёт
        // только restore-хук) — ничего не записав по произвольному пути из манифеста.
        var archivePath = Path.Combine(Path.GetTempPath(), $"ebk-evil2-{Guid.NewGuid():N}.ebk");
        var hostileTarget = Path.Combine(Path.GetTempPath(), $"ebk-pwn-{Guid.NewGuid():N}.txt");
        try
        {
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var data = zip.CreateEntry("data/evil/payload");
                await using (var ds = data.Open())
                    await ds.WriteAsync(System.Text.Encoding.UTF8.GetBytes("pwned"));

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
                                    TokenPath = hostileTarget.Replace('\\', '/'),
                                    Type = PathEntryType.File,
                                    ArchivePath = "evil/payload",
                                    ManagedByModule = true
                                }
                            ]
                        }
                    ]
                };
                await JsonSerializer.SerializeAsync(s, manifest, ManifestJson.Options);
            }

            // Выбрано всё, «в исходные места» — managed-запись молча пропускается.
            await new BackupEngine().RestoreAsync(
                archivePath, destinationRootOverride: null, entryFilter: _ => true);
            Assert.False(File.Exists(hostileTarget)); // ничего не записано по враждебному пути
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (File.Exists(hostileTarget)) File.Delete(hostileTarget);
        }
    }

    [Fact]
    public async Task Restore_Rejects_Traversal_In_TokenPath()
    {
        // Не-managed запись с «..» в TokenPath — полное восстановление должно отказать.
        var archivePath = Path.Combine(Path.GetTempPath(), $"ebk-trav-{Guid.NewGuid():N}.ebk");
        try
        {
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var data = zip.CreateEntry("data/x/payload");
                await using (var ds = data.Open())
                    await ds.WriteAsync(System.Text.Encoding.UTF8.GetBytes("x"));

                var entry = zip.CreateEntry("manifest.json");
                await using var s = entry.Open();
                var manifest = new Manifest
                {
                    CreatedAt = default,
                    Modules =
                    [
                        new ModuleEntry
                        {
                            ModuleId = "x",
                            DisplayName = "x",
                            Entries =
                            [
                                new PathEntry
                                {
                                    TokenPath = "{APPDATA}/../../../Windows/evil",
                                    Type = PathEntryType.File,
                                    ArchivePath = "x/payload"
                                }
                            ]
                        }
                    ]
                };
                await JsonSerializer.SerializeAsync(s, manifest, ManifestJson.Options);
            }

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new BackupEngine().RestoreAsync(archivePath));
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
        }
    }
}
