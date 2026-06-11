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
