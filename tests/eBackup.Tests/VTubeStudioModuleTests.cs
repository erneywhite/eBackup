using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using eBackup.Core.Model;
using eBackup.Modules.VTubeStudio;
using Xunit;

namespace eBackup.Tests;

public class VTubeStudioModuleTests : IDisposable
{
    private readonly string _root;
    public VTubeStudioModuleTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-vts-{Guid.NewGuid():N}");
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    private string MakeStreamingAssets(params string[] subdirs)
    {
        var sa = Path.Combine(_root, "StreamingAssets");
        foreach (var d in subdirs)
            Directory.CreateDirectory(Path.Combine(sa, d));
        return sa;
    }

    [Fact]
    public async Task Discovers_Only_User_Content_Subfolders()
    {
        var sa = MakeStreamingAssets(
            // забираем (пользовательский контент)
            "Config", "Live2DModels", "Items", "Backgrounds", "Effects", "WorkshopCredits", "Backup",
            // НЕ забираем (трекеры/шипованное/мусор)
            "MXTracker", "OpenSeeFaceTracker_v_1_20_2", "Language", "Logs", "TempVTSFiles", "WorkshopPreviews");

        var entries = await new VTubeStudioBackupModule(streamingAssetsOverride: sa).DiscoverAsync();

        Assert.Equal(
            new[]
            {
                "vtube-studio/backgrounds", "vtube-studio/backup", "vtube-studio/config",
                "vtube-studio/effects", "vtube-studio/items", "vtube-studio/live2dmodels",
                "vtube-studio/workshopcredits",
            },
            entries.Select(e => e.ArchivePath).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // Тяжёлые трекеры и шипованное НЕ попали в бэкап.
        Assert.DoesNotContain(entries, e => e.TokenPath.Contains("MXTracker"));
        Assert.DoesNotContain(entries, e => e.TokenPath.Contains("OpenSeeFace"));
        Assert.DoesNotContain(entries, e => e.TokenPath.EndsWith("/Language") || e.TokenPath.EndsWith("/Logs"));

        // Все записи — директории с лог-маской и абсолютным путём.
        Assert.All(entries, e =>
        {
            Assert.Equal(PathEntryType.Directory, e.Type);
            Assert.Contains("**/*.log", e.ExcludeGlobs);
            Assert.True(Path.IsPathFullyQualified(e.TokenPath.Replace('/', Path.DirectorySeparatorChar)));
        });
    }

    [Fact]
    public async Task Missing_Subfolders_Are_Skipped()
    {
        var sa = MakeStreamingAssets("Config", "Live2DModels"); // только часть папок присутствует
        var entries = await new VTubeStudioBackupModule(streamingAssetsOverride: sa).DiscoverAsync();
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task No_VTube_Studio_Yields_Empty()
    {
        // override на несуществующий путь → модуль ничего не находит (как при отсутствии игры).
        var entries = await new VTubeStudioBackupModule(streamingAssetsOverride: Path.Combine(_root, "nope")).DiscoverAsync();
        Assert.Empty(entries);
    }

    [Fact]
    public void SteamLibraries_Parses_Vdf_Paths_And_Includes_Root()
    {
        var steamRoot = Path.Combine(_root, "Steam");
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
        var lib2 = Path.Combine(_root, "SteamLib2");
        Directory.CreateDirectory(lib2);
        var ghost = Path.Combine(_root, "Removed"); // в vdf есть, но папки нет — отбрасывается

        var vdf = $"\"path\"\t\t\"{Esc(steamRoot)}\"\n\"path\"\t\t\"{Esc(lib2)}\"\n\"path\"\t\t\"{Esc(ghost)}\"\n";
        File.WriteAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"), vdf);

        var libs = SteamLibraries.LibraryPaths(steamRoot);

        Assert.Contains(steamRoot, libs);                 // сам корень всегда
        Assert.Contains(lib2, libs);                      // существующая библиотека из vdf
        Assert.DoesNotContain(ghost, libs);               // несуществующая — отброшена

        static string Esc(string p) => p.Replace("\\", "\\\\"); // в vdf слэши экранированы
    }
}
