using System.Globalization;
using System.Resources;

namespace eBackup.Localization;

/// <summary>
/// Локализованные строки back-end (служба/ядро/хранилища), которые доходят до пользователя в GUI:
/// фазы прогресса, журнал запуска, успех «Теста» хранилищ, типизированные IPC-ошибки.
/// Ключи — в Strings.resx (нейтраль = английский) + Strings.ru.resx (русский, сателлит-сборка).
/// Язык задаётся <see cref="SetCulture"/>: служба ставит его при старте и при запросах от GUI;
/// фоновые/плановые прогоны используют последний установленный (по умолчанию — русский).
/// </summary>
public static class L
{
    private static readonly ResourceManager Rm = new("eBackup.Localization.Strings", typeof(L).Assembly);

    public static string Get(string key) => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    // args допускает null-элементы — string.Format отдаёт для них пустую строку.
    public static string Get(string key, params object?[] args) => string.Format(Get(key), args);

    /// <summary>Язык для текущего и новых потоков ("ru"/"en"); пусто → русский (поведение по умолчанию).</summary>
    public static void SetCulture(string? lang)
    {
        CultureInfo culture;
        try
        {
            culture = string.IsNullOrWhiteSpace(lang)
                ? CultureInfo.GetCultureInfo("ru")
                : CultureInfo.GetCultureInfo(lang);
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.GetCultureInfo("ru");
        }

        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
