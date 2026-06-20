using System.Globalization;
using eBackup.Localization;
using Xunit;

namespace eBackup.Tests;

/// <summary>Back-end локализация (служба/ядро): нейтраль = английский, сателлит ru. Проверяем резолв по культуре.</summary>
public class LocalizationTests
{
    [Fact]
    public void Backend_strings_resolve_per_culture()
    {
        var prev = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            Assert.Equal("Заливка…", L.Get("Phase_Uploading"));
            Assert.Equal("Восстанавливаю: OBS…", L.Get("Phase_Restoring", "OBS"));

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            Assert.Equal("Uploading…", L.Get("Phase_Uploading"));
            Assert.Equal("Restoring: OBS…", L.Get("Phase_Restoring", "OBS"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = prev;
        }
    }

    [Fact]
    public void Missing_key_returns_key_not_throws()
    {
        Assert.Equal("Nonexistent_Key_123", L.Get("Nonexistent_Key_123"));
    }
}
