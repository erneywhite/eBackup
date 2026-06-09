using System.Text.Json;
using eBackup.Core.Abstractions;
using eBackup.Core.Model;
using eBackup.Core.Modules;
using eBackup.Core.Paths;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage.Sftp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

/// <summary>
/// Страница настройки и запуска бэкапа (вместо модального диалога — часть интерфейса).
/// Помимо модулей умеет бэкапить произвольные «свои папки» (список сохраняется между
/// сессиями) — они идут в архив обычным декларативным модулем "folders".
/// </summary>
public sealed partial class BackupPage : Page
{
    private readonly ModuleRegistry _registry = new(
    [
        new BuiltInModuleSource([new ObsBackupModule()]),
        new DeclarativeModuleSource(),
    ]);
    private readonly SftpConnectionStore _store = new(new DpapiSecretProtector());

    private readonly List<CheckBox> _moduleChecks = [];
    private readonly List<CheckBox> _sftpChecks = [];
    private List<string> _folders = [];

    private static string FoldersConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "eBackup", "custom-folders.json");

    public BackupPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        // Модули из реестра
        ModulesPanel.Children.Clear();
        _moduleChecks.Clear();
        foreach (var d in _registry.Discover().Where(d => d.Problem is null && d.Instance is not null))
        {
            var cb = new CheckBox { Content = d.DisplayName, IsChecked = true, Tag = d.Instance };
            _moduleChecks.Add(cb);
            ModulesPanel.Children.Add(cb);
        }

        // Свои папки (сохраняются между сессиями)
        _folders = LoadFolders();
        RenderFolders();

        // Цели: сохранённые SFTP-подключения
        SftpPanel.Children.Clear();
        _sftpChecks.Clear();
        List<SavedSftpConnection> connections;
        try
        {
            connections = (await _store.LoadAsync()).ToList();
        }
        catch
        {
            connections = [];
        }
        foreach (var c in connections)
        {
            var cb = new CheckBox { Content = $"{c.Name}  ({c.Username}@{c.Host}:{c.Port})", Tag = c };
            _sftpChecks.Add(cb);
            SftpPanel.Children.Add(cb);
        }

        LaunchBtn.IsEnabled = MainWindow.Instance?.IsBusy != true;
    }

    // ---------- свои папки ----------

    private static List<string> LoadFolders()
    {
        try
        {
            if (File.Exists(FoldersConfigPath))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FoldersConfigPath)) ?? [];
        }
        catch
        {
            // повреждённый конфиг папок — просто начнём с пустого списка
        }
        return [];
    }

    private void SaveFolders()
    {
        try
        {
            var dir = Path.GetDirectoryName(FoldersConfigPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FoldersConfigPath, JsonSerializer.Serialize(_folders));
        }
        catch
        {
            // несохранённый список не критичен для текущего запуска
        }
    }

    private void RenderFolders()
    {
        FoldersPanel.Children.Clear();
        var dim = (Brush)Application.Current.Resources["EbTextDimBrush"];

        if (_folders.Count == 0)
        {
            FoldersPanel.Children.Add(new TextBlock
            {
                Text = "пока пусто — добавь любые папки, которые хочешь сохранять",
                FontSize = 12,
                Foreground = dim
            });
            return;
        }

        foreach (var folder in _folders)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = folder,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(text, 0);
            row.Children.Add(text);

            var remove = new Button
            {
                Content = "✕",
                Padding = new Thickness(8, 2, 8, 2),
                CornerRadius = new CornerRadius(8),
                Tag = folder
            };
            remove.Click += (_, _) =>
            {
                _folders.Remove(folder);
                SaveFolders();
                RenderFolders();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);

            FoldersPanel.Children.Add(row);
        }
    }

    private async void AddFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null)
            return;

        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || _folders.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
            return;

        _folders.Add(folder.Path);
        SaveFolders();
        RenderFolders();
    }

    /// <summary>Свои папки как обычный модуль (логика — в Core.Modules.CustomFolders).</summary>
    private DeclarativeModule? BuildFoldersModule() => CustomFolders.Build(_folders);

    // ---------- запуск ----------

    private void EncryptCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (Pass1 is null || Pass2 is null)
            return;
        var on = EncryptCheck.IsChecked == true;
        Pass1.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Pass2.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LaunchBtn_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null || MainWindow.Instance.IsBusy)
            return;

        var modules = _moduleChecks.Where(cb => cb.IsChecked == true)
            .Select(cb => (IBackupModule)cb.Tag)
            .ToList();
        var foldersModule = BuildFoldersModule();
        if (foldersModule is not null)
            modules.Add(foldersModule);

        var targets = _sftpChecks.Where(cb => cb.IsChecked == true)
            .Select(cb => (SavedSftpConnection)cb.Tag)
            .ToList();
        var keepLocal = LocalCheck.IsChecked == true;

        string? err = null;
        if (modules.Count == 0)
            err = "Выбери хотя бы один модуль или добавь папку.";
        else if (!keepLocal && targets.Count == 0)
            err = "Выбери хотя бы одно хранилище.";
        else if (EncryptCheck.IsChecked == true)
        {
            if (Pass1.Password.Length == 0)
                err = "Введи парольную фразу.";
            else if (Pass1.Password != Pass2.Password)
                err = "Парольные фразы не совпадают.";
        }

        if (err is not null)
        {
            ErrorText.Text = err;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        ErrorText.Visibility = Visibility.Collapsed;

        var passphrase = EncryptCheck.IsChecked == true ? Pass1.Password : null;
        LaunchBtn.IsEnabled = false;
        try
        {
            await MainWindow.Instance.StartBackupAsync(new BackupRequest(modules, keepLocal, targets, passphrase));
        }
        finally
        {
            LaunchBtn.IsEnabled = true;
        }
    }
}
