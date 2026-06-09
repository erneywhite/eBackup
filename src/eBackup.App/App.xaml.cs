using Microsoft.UI.Xaml;

namespace eBackup.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // Страховка: необработанное исключение (например, из async void обработчика)
        // не должно молча убивать приложение — логируем и продолжаем работать.
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "eBackup", "logs");
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
        _window = new MainWindow();
        _window.Activate();
    }
}
