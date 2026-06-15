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

    // ---------- S1: containment при restore-в-исходные места ----------

    /// <summary>Собрать архив из одной файловой записи с заданным TokenPath (для тестов containment).</summary>
    private static async Task WriteSingleFileArchive(string archivePath, string tokenPath, string archiveRel, byte[] payload)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var data = zip.CreateEntry("data/" + archiveRel);
        await using (var ds = data.Open())
            await ds.WriteAsync(payload);

        var entry = zip.CreateEntry("manifest.json");
        await using var s = entry.Open();
        var manifest = new Manifest
        {
            CreatedAt = default,
            Modules =
            [
                new ModuleEntry
                {
                    ModuleId = "m",
                    DisplayName = "m",
                    Entries =
                    [
                        new PathEntry { TokenPath = tokenPath, Type = PathEntryType.File, ArchivePath = archiveRel }
                    ]
                }
            ]
        };
        await JsonSerializer.SerializeAsync(s, manifest, ManifestJson.Options);
    }

    [Fact]
    public async Task Restore_Rejects_DriveAbsolute_Tail_After_Token()
    {
        // «{APPDATA}/C:/…» проходит HasTraversal (нет «..»), но Path.Combine отдаёт
        // абсолютный хвост наружу. Канонизированный containment должен это отклонить.
        var realAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var escaped = Path.Combine(Path.GetDirectoryName(realAppData)!, $"ebk-escape-{Guid.NewGuid():N}.txt");
        var archivePath = Path.Combine(Path.GetTempPath(), $"ebk-esc-{Guid.NewGuid():N}.ebk");
        try
        {
            await WriteSingleFileArchive(archivePath, "{APPDATA}/" + escaped.Replace('\\', '/'),
                "m/payload", System.Text.Encoding.UTF8.GetBytes("pwned"));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new BackupEngine().RestoreAsync(archivePath));
            Assert.False(File.Exists(escaped)); // ничего не записано за пределами корня токена
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (File.Exists(escaped)) File.Delete(escaped);
        }
    }

    [Fact]
    public async Task Restore_Allows_RawAbsolute_To_Original()
    {
        // Сырой абсолютный путь (как D:\Servers\… — игросервер): токенового корня нет,
        // граница — сам архив, restore разрешён. Не должен ломаться нашей проверкой.
        var destDir = Path.Combine(Path.GetTempPath(), $"ebk-srv-{Guid.NewGuid():N}");
        var destFile = Path.Combine(destDir, "server.properties");
        var archivePath = Path.Combine(Path.GetTempPath(), $"ebk-raw-{Guid.NewGuid():N}.ebk");
        try
        {
            await WriteSingleFileArchive(archivePath, destFile.Replace('\\', '/'),
                "m/server.properties", System.Text.Encoding.UTF8.GetBytes("level-name=world"));

            await new BackupEngine().RestoreAsync(archivePath);
            Assert.True(File.Exists(destFile));
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_Allows_Tokenized_Within_Root()
    {
        // Легитимный токенизированный путь без «..» — restore-в-исходные работает как раньше.
        var sub = $"ebk-test-{Guid.NewGuid():N}";
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var destFile = Path.Combine(localAppData, sub, "config.ini");
        var archivePath = Path.Combine(Path.GetTempPath(), $"ebk-tok-{Guid.NewGuid():N}.ebk");
        try
        {
            await WriteSingleFileArchive(archivePath, "{LOCALAPPDATA}/" + sub + "/config.ini",
                "m/config.ini", System.Text.Encoding.UTF8.GetBytes("ok=1"));

            await new BackupEngine().RestoreAsync(archivePath);
            Assert.True(File.Exists(destFile));
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            var dir = Path.Combine(localAppData, sub);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("{APPDATA}/x", true)]
    [InlineData("{LOCALAPPDATA}", true)]
    [InlineData("{PROGRAMDATA}/ssh", true)]
    [InlineData("D:/Servers/mc", false)]      // сырой абсолютный — токенового корня нет
    [InlineData("C:/Windows", false)]
    [InlineData("plain/rel", false)]
    public void TryGetTokenRoot_Detects_Tokenized_Paths(string tokenPath, bool expected)
        => Assert.Equal(expected, PathTokens.TryGetTokenRoot(tokenPath, out _));
}
