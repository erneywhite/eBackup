using eBackup.Localization;
using eBackup.Platform;

namespace eBackup.Service;

/// <summary>
/// Язык строк службы/ядра (фазы прогресса, журнал запуска, типизированные ошибки). GUI присылает его
/// в Hello; служба применяет к текущему/новым потокам и запоминает в %ProgramData%\eBackup\config\ui-language,
/// чтобы фоновые и плановые прогоны (когда GUI не запущен) тоже шли на нужном языке. По умолчанию — русский.
/// </summary>
internal static class ServiceLanguage
{
    /// <summary>Применить язык, сохранённый прошлым запуском (вызывается при старте службы).</summary>
    public static void ApplyPersisted()
    {
        try
        {
            var lang = File.Exists(AppPaths.MachineLanguageFile)
                ? File.ReadAllText(AppPaths.MachineLanguageFile).Trim()
                : null;
            L.SetCulture(lang);
        }
        catch
        {
            L.SetCulture(null); // не смогли прочитать — русский по умолчанию
        }
    }

    /// <summary>Сменить язык (по Hello от GUI) и запомнить его машинно.</summary>
    public static void Set(string? lang)
    {
        L.SetCulture(lang);
        if (string.IsNullOrWhiteSpace(lang))
            return;
        try
        {
            Directory.CreateDirectory(AppPaths.MachineConfigDir);
            File.WriteAllText(AppPaths.MachineLanguageFile, lang.Trim());
        }
        catch
        {
            // запись best-effort: язык всё равно применён к текущему процессу
        }
    }
}
