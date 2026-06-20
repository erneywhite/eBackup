using eBackup.Platform;
using Microsoft.UI.Xaml;

namespace eBackup.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // Язык интерфейса — ДО создания окна: настройка пользователя, иначе по языку системы (ru → ru,
        // иначе en). Дальше resw-строки (x:Uid + Loc) резолвятся под выбранный язык. Смена — с перезапуском.
        try
        {
            var lang = AppSettings.Load().Language;
            if (string.IsNullOrEmpty(lang))
                lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = lang;
        }
        catch { /* не критично — останется язык по умолчанию */ }

        InitializeComponent();

        // Страховка: необработанное исключение (например, из async void обработчика)
        // не должно молча убивать приложение — логируем и продолжаем работать.
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            try
            {
                var dir = AppPaths.LogsDir;
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "app.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}");
            }
            catch
            {
                // даже сбой логирования не должен ронять приложение
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;

        // Автозапуск с Windows стартует с «--minimized» — сразу в трей, без окна.
        if (Environment.GetCommandLineArgs().Contains("--minimized"))
        {
            window.StartHidden();
            return;
        }

        window.Activate();

        // Ассоциация .ebk: двойной клик по архиву открывает его в браузере архива.
        var fileArg = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)
                && a.EndsWith(".ebk", StringComparison.OrdinalIgnoreCase)
                && File.Exists(a));
        if (fileArg is not null)
            window.OpenLocalArchive(fileArg);
    }
}
