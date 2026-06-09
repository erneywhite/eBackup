using System.Linq;
using System.Threading.Tasks;
using eBackup.Core.Modules;
using Xunit;

namespace eBackup.Tests;

public class CustomFoldersTests
{
    [Fact]
    public async Task Keeps_Original_Folder_Names_Including_Cyrillic()
    {
        var module = CustomFolders.Build([@"S:\Downloads\Comet\Новая папка"])!;
        var entry = (await module.DiscoverAsync()).Single();

        Assert.Equal("folders/Новая папка", entry.ArchivePath);
    }

    [Fact]
    public async Task Duplicate_Leaf_Names_Get_Suffix_Only_On_Collision()
    {
        var module = CustomFolders.Build([@"C:\A\Музыка", @"D:\B\Музыка"])!;
        var paths = (await module.DiscoverAsync()).Select(e => e.ArchivePath).ToList();

        Assert.Equal("folders/Музыка", paths[0]);
        Assert.Equal("folders/Музыка (2)", paths[1]);
    }

    [Fact]
    public async Task Drive_Root_Gets_Readable_Name()
    {
        var module = CustomFolders.Build([@"S:\"])!;
        var entry = (await module.DiscoverAsync()).Single();

        Assert.Equal("folders/диск-S", entry.ArchivePath);
    }
}
