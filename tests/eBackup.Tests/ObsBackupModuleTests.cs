using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using eBackup.Core.Engine;
using eBackup.Core.Model;
using eBackup.Modules.Obs;
using Xunit;

namespace eBackup.Tests;

public class ObsBackupModuleTests
{
    [Fact]
    public async Task Discovers_External_Image_Asset_From_Scene()
    {
        var obsRoot = Path.Combine(Path.GetTempPath(), $"obs-root-{Guid.NewGuid():N}");
        var assetFile = Path.Combine(Path.GetTempPath(), $"bg-{Guid.NewGuid():N}.jpg");
        try
        {
            var scenesDir = Path.Combine(obsRoot, "basic", "scenes");
            Directory.CreateDirectory(scenesDir);
            await File.WriteAllTextAsync(assetFile, "fake-image-bytes");

            var sceneJson = $$"""
                {
                  "sources": [
                    { "id": "image_source", "settings": { "file": "{{assetFile.Replace("\\", "/")}}" } },
                    { "id": "color_source", "settings": { "color": 4294967295 } }
                  ]
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(scenesDir, "Test.json"), sceneJson);

            var module = new ObsBackupModule(obsRoot);
            var entries = await module.DiscoverAsync();

            var asset = entries.SingleOrDefault(e => e.ManagedByModule);
            Assert.NotNull(asset);
            Assert.Equal(PathEntryType.File, asset!.Type);
            Assert.StartsWith("obs/assets/", asset.ArchivePath);
            Assert.EndsWith(".jpg", asset.ArchivePath);
        }
        finally
        {
            if (Directory.Exists(obsRoot)) Directory.Delete(obsRoot, recursive: true);
            if (File.Exists(assetFile)) File.Delete(assetFile);
        }
    }

    [Fact]
    public async Task Ignores_NonLocal_And_Missing_Assets()
    {
        var obsRoot = Path.Combine(Path.GetTempPath(), $"obs-root-{Guid.NewGuid():N}");
        try
        {
            var scenesDir = Path.Combine(obsRoot, "basic", "scenes");
            Directory.CreateDirectory(scenesDir);

            // Картинка по несуществующему пути + браузерный URL — ничего захватываться не должно.
            var sceneJson = """
                {
                  "sources": [
                    { "id": "image_source", "settings": { "file": "C:/nope/missing-xyz.png" } },
                    { "id": "browser_source", "settings": { "url": "https://example.com" } }
                  ]
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(scenesDir, "Test.json"), sceneJson);

            var module = new ObsBackupModule(obsRoot);
            var entries = await module.DiscoverAsync();

            Assert.DoesNotContain(entries, e => e.ManagedByModule);
        }
        finally
        {
            if (Directory.Exists(obsRoot)) Directory.Delete(obsRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_Places_Assets_And_Rewrites_Scene_Paths()
    {
        var obsRoot = Path.Combine(Path.GetTempPath(), $"obs-root-{Guid.NewGuid():N}");
        var assetFile = Path.Combine(Path.GetTempPath(), $"bg-{Guid.NewGuid():N}.jpg");
        var outDir = Path.Combine(Path.GetTempPath(), $"ebk-out-{Guid.NewGuid():N}");
        var restoreDir = Path.Combine(Path.GetTempPath(), $"ebk-restore-{Guid.NewGuid():N}");
        var assetsDir = Path.Combine(Path.GetTempPath(), $"ebk-assets-{Guid.NewGuid():N}");
        try
        {
            var scenesDir = Path.Combine(obsRoot, "basic", "scenes");
            Directory.CreateDirectory(scenesDir);
            await File.WriteAllTextAsync(assetFile, "fake-image-bytes");

            var assetFwd = assetFile.Replace("\\", "/");
            var sceneJson = $$"""
                { "sources": [ { "id": "image_source", "settings": { "file": "{{assetFwd}}" } } ] }
                """;
            await File.WriteAllTextAsync(Path.Combine(scenesDir, "Test.json"), sceneJson);

            var module = new ObsBackupModule(obsRoot);
            var engine = new BackupEngine();
            var archive = await engine.CreateBackupAsync([module], outDir, "rt");

            await engine.RestoreAsync(
                archive,
                modules: [module],
                destinationRootOverride: restoreDir,
                assetsDirectory: assetsDir);

            // Ассет извлечён в выбранную папку.
            var restoredAsset = Path.Combine(assetsDir, "0", Path.GetFileName(assetFile));
            Assert.True(File.Exists(restoredAsset));

            // В восстановленной сцене путь переписан на новую папку; старого пути нет.
            var restoredScene = await File.ReadAllTextAsync(
                Path.Combine(restoreDir, "obs", "obs-studio", "basic", "scenes", "Test.json"));
            Assert.Contains(restoredAsset.Replace("\\", "/"), restoredScene);
            Assert.DoesNotContain(assetFwd, restoredScene);
        }
        finally
        {
            foreach (var d in new[] { obsRoot, outDir, restoreDir, assetsDir })
                if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
            if (File.Exists(assetFile)) File.Delete(assetFile);
        }
    }

    [Fact]
    public async Task Discovers_Plugin_Folders_From_Install_Root()
    {
        var obsRoot = Path.Combine(Path.GetTempPath(), $"obs-root-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(Path.GetTempPath(), $"obs-install-{Guid.NewGuid():N}");
        try
        {
            var bin = Path.Combine(installRoot, "obs-plugins", "64bit");
            Directory.CreateDirectory(bin);
            await File.WriteAllTextAsync(Path.Combine(bin, "sample.dll"), "dll");

            var data = Path.Combine(installRoot, "data", "obs-plugins", "sample");
            Directory.CreateDirectory(data);
            await File.WriteAllTextAsync(Path.Combine(data, "info.txt"), "data");

            var module = new ObsBackupModule(obsRoot, installRoot);
            var entries = await module.DiscoverAsync();

            Assert.Contains(entries, e => e.ArchivePath == "obs/install/obs-plugins" && e.Type == PathEntryType.Directory);
            Assert.Contains(entries, e => e.ArchivePath == "obs/install/data-obs-plugins" && e.Type == PathEntryType.Directory);
        }
        finally
        {
            foreach (var d in new[] { obsRoot, installRoot })
                if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        }
    }
}
