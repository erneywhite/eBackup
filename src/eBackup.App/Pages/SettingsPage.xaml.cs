using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        var settings = AppSettings.Load();
        RetentionBox.Text = settings.RetentionCount.ToString();
        TrayToggle.IsOn = settings.MinimizeToTray;
        AutostartToggle.IsOn = Autostart.IsEnabled();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"v{version.ToString(3)}";
    }

    // ---------- ссылки и быстрые папки ----------

    private async void Coffee_Click(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://dalink.to/toristarm"));

    private async void GitHub_Click(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/erneywhite/eBackup"));

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
        => OpenFolder(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eBackup"));

    private void OpenModules_Click(object sender, RoutedEventArgs e)
        => OpenFolder(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eBackup", "modules"));

    private void OpenLog_Click(object sender, RoutedEventArgs e)
        => OpenFolder(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "eBackup", "logs"));

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path); // папка могла ещё не появиться — создаём, чтоб было что открыть
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus("✕ Не удалось открыть папку: " + ex.Message, ok: false);
        }
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RetentionBox.Text.Trim(), out var retention) || retention < 0)
        {
            SetStatus("«Хранить последних» — целое число от 0.", ok: false);
            return;
        }

        try
        {
            // Меняем только свои поля — остальное (флаги инициализации и т.п.) не трогаем.
            var settings = AppSettings.Load();
            settings.RetentionCount = retention;
            settings.MinimizeToTray = TrayToggle.IsOn;
            settings.Save();

            Autostart.Set(AutostartToggle.IsOn);
            SetStatus("✓ Сохранено", ok: true);
        }
        catch (Exception ex)
        {
            SetStatus("✕ " + ex.Message, ok: false);
        }
    }

    private void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)Application.Current.Resources[ok ? "EbOkBrush" : "EbErrBrush"];
    }
}
