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
        NotifyToggle.IsOn = settings.NotifyOnBackgroundBackup;
        MachineNameToggle.IsOn = settings.IncludeMachineNameInArchive;
        CompressionBox.SelectedIndex = Math.Clamp(settings.CompressionMode, 0, 2);
        AssetsDirBox.Text = settings.DefaultAssetsDir ?? string.Empty;
        SelfTestBox.Text = settings.SelfTestMinutes.ToString();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"v{version.ToString(3)}";

        // Карточка обновлений: текущая версия + текущее состояние UpdateService.
        UpdCurrentText.Text = $"Текущая версия: {UpdateService.AppVersion()}";
        UpdateService.Changed += OnUpdateState;
        OnUpdateState(UpdateService.Current);
        Unloaded += (_, _) => UpdateService.Changed -= OnUpdateState;
    }

    private void OnUpdateState(UpdateState s)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdProgress.Visibility = Visibility.Collapsed;  // прогрессбар показываем только при скачивании
            switch (s.Stage)
            {
                case UpdateStage.Checking:
                    UpdStatusText.Text = "Проверяю наличие обновлений…";
                    UpdActionBtn.Content = "Проверяю…";
                    UpdActionBtn.IsEnabled = false;
                    break;
                case UpdateStage.Available:
                    UpdStatusText.Text = $"Доступна новая версия {s.Version}.";
                    UpdActionBtn.Content = "Скачать и установить";
                    UpdActionBtn.IsEnabled = true;
                    break;
                case UpdateStage.Downloading:
                    UpdStatusText.Text = $"Скачиваю обновление… {s.Progress * 100:0}%";
                    UpdProgress.Value = s.Progress * 100;
                    UpdProgress.Visibility = Visibility.Visible;
                    UpdActionBtn.Content = "Скачиваю…";
                    UpdActionBtn.IsEnabled = false;
                    break;
                case UpdateStage.ReadyToInstall:
                    UpdStatusText.Text = "Устанавливаю и перезапускаю…";
                    UpdActionBtn.IsEnabled = false;
                    break;
                case UpdateStage.Failed:
                    UpdStatusText.Text = "✕ " + (s.Error ?? "ошибка проверки");
                    UpdActionBtn.Content = "Проверить обновления";
                    UpdActionBtn.IsEnabled = true;
                    break;
                case UpdateStage.UpToDate:
                    UpdStatusText.Text = "Установлена последняя версия.";
                    UpdActionBtn.Content = "Проверить обновления";
                    UpdActionBtn.IsEnabled = true;
                    break;
                default:
                    UpdStatusText.Text = string.Empty;
                    UpdActionBtn.Content = "Проверить обновления";
                    UpdActionBtn.IsEnabled = true;
                    break;
            }
        });
    }

    private async void UpdAction_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Current.Stage == UpdateStage.Available)
        {
            if (MainWindow.Instance is not null)
                await MainWindow.Instance.StartUpdateInstallAsync();
        }
        else
        {
            await UpdateService.CheckAsync(quiet: false);
        }
    }

    private async void PickAssetsDir_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null)
            return;
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            AssetsDirBox.Text = folder.Path;
    }

    private void CleanTemp_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance?.IsBusy == true)
        {
            SetStatus("Идёт бэкап или восстановление — временные файлы сейчас нужны.", ok: false);
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "eBackup");
        try
        {
            long freed = 0;
            if (Directory.Exists(tempDir))
            {
                freed = Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories)
                    .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                Directory.Delete(tempDir, recursive: true);
            }
            SetStatus(freed == 0
                ? "✓ Временных файлов нет — и так чисто."
                : $"✓ Очищено: освобождено {freed / 1024.0 / 1024.0:0.#} МБ.", ok: true);
        }
        catch (Exception ex)
        {
            SetStatus("✕ Не удалось очистить: " + ex.Message, ok: false);
        }
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

        if (!int.TryParse(SelfTestBox.Text.Trim(), out var selfTest) || selfTest is < 0 or > 1440)
        {
            SetStatus("Проверка хранилищ — число минут от 0 до 1440.", ok: false);
            return;
        }

        try
        {
            // Меняем только свои поля — остальное (флаги инициализации и т.п.) не трогаем.
            var settings = AppSettings.Load();
            settings.RetentionCount = retention;
            settings.MinimizeToTray = TrayToggle.IsOn;
            settings.NotifyOnBackgroundBackup = NotifyToggle.IsOn;
            settings.IncludeMachineNameInArchive = MachineNameToggle.IsOn;
            settings.CompressionMode = Math.Clamp(CompressionBox.SelectedIndex, 0, 2);
            var assetsDir = AssetsDirBox.Text.Trim();
            settings.DefaultAssetsDir = assetsDir.Length == 0 ? null : assetsDir;
            settings.SelfTestMinutes = selfTest;
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
