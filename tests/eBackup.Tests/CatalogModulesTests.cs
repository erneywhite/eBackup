using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using eBackup.Core.Modules;
using eBackup.Core.Paths;
using Xunit;

namespace eBackup.Tests;

/// <summary>
/// Защитный тест каталога: реальные `catalog/modules/*.module.json` должны проходить валидацию
/// текущим контрактом (а не только синтетические дескрипторы). Ловит опечатку в токене/archivePath
/// или несовместимость с версией до релиза. Пути всех записей — с известным токеном или raw-absolute,
/// то есть резолвятся (в т.ч. per-SID под службой 1.2).
/// </summary>
public class CatalogModulesTests
{
    private static string CatalogModulesDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "catalog", "modules");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("catalog/modules не найден вверх по дереву от " + AppContext.BaseDirectory);
    }

    [Fact]
    public async Task All_Catalog_Modules_Validate_And_Resolve()
    {
        var dir = CatalogModulesDir();
        var files = Directory.GetFiles(dir, "*.module.json");
        Assert.NotEmpty(files); // каталог не должен оказаться пустым

        var descriptors = new DeclarativeModuleSource(dir).Discover().ToList();
        Assert.Equal(files.Length, descriptors.Count); // каждый файл распознан как дескриптор

        foreach (var d in descriptors)
        {
            // Валиден: не заблокирован (токен/archivePath/контракт в порядке), есть инстанс.
            Assert.True(d.Trust == ModuleTrust.Trusted, $"модуль «{d.Id}» заблокирован: {d.Problem}");
            Assert.NotNull(d.Instance);

            // Каждый путь записи резолвится: либо известный токен ({APPDATA}/{USERPROFILE}/…),
            // либо raw-absolute (личные пути). Так гарантируем работу под службой (per-SID-резолвер
            // знает все эти токены) и отсутствие «висящих» сегментов.
            var entries = await d.Instance!.DiscoverAsync();
            Assert.NotEmpty(entries);
            foreach (var e in entries)
            {
                Assert.False(PathTokens.HasTraversal(e.TokenPath), $"{d.Id}: обход в пути {e.TokenPath}");
                var hasToken = PathTokens.TryGetTokenPrefix(e.TokenPath, out _);
                var isAbsolute = Path.IsPathFullyQualified(e.TokenPath.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(hasToken || isAbsolute, $"{d.Id}: путь не резолвится — {e.TokenPath}");
            }
        }
    }
}
