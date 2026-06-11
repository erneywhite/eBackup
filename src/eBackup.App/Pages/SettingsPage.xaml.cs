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
        DirBox.Text = settings.LocalBackupDir;
        RetentionBox.Text = settings.RetentionCount.ToString();
        TrayToggle.IsOn = settings.MinimizeToTray;
        AutostartToggle.IsOn = Autostart.IsEnabled();
    }

    private async void PickDirBtn_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null)
            return;
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            DirBox.Text = folder.Path;
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        var dir = DirBox.Text.Trim();
        if (dir.Length == 0)
        {
            SetStatus("Укажи папку для бэкапов.", ok: false);
            return;
        }

        if (!int.TryParse(RetentionBox.Text.Trim(), out var retention) || retention < 0)
        {
            SetStatus("«Хранить последних» — целое число от 0.", ok: false);
            return;
        }

        try
        {
            Directory.CreateDirectory(dir); // заодно проверяем, что путь рабочий
            new AppSettings
            {
                LocalBackupDir = dir,
                RetentionCount = retention,
                MinimizeToTray = TrayToggle.IsOn
            }.Save();
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
        StatusText.Foreground = (Brush)Resources[ok ? "EbOkBrush" : "EbErrBrush"];
    }
}
