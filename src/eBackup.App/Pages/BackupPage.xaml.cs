using eBackup.Core.Abstractions;
using eBackup.Core.Modules;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage;
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
    private readonly StorageStore _storages = new(new DpapiSecretProtector());

    private readonly List<CheckBox> _moduleChecks = [];
    private readonly List<CheckBox> _storageChecks = [];
    private List<string> _folders = [];

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
        foreach (var d in _registry.Discover().Where(d => d.Problem is null && d.Instance is not null && d.Enabled))
        {
            var cb = new CheckBox { Content = d.DisplayName, IsChecked = true, Tag = d.Instance };
            _moduleChecks.Add(cb);
            ModulesPanel.Children.Add(cb);
        }

        // Свои папки (сохраняются между сессиями)
        _folders = LoadFolders();
        RenderFolders();

        // Цели: все хранилища из единого списка (папки отмечены по умолчанию)
        StoragesPanel.Children.Clear();
        _storageChecks.Clear();
        List<SavedStorage> storages;
        try
        {
            storages = (await _storages.LoadAsync()).ToList();
        }
        catch
        {
            storages = [];
        }

        if (storages.Count == 0)
        {
            StoragesPanel.Children.Add(new TextBlock
            {
                Text = "хранилищ нет — добавь на странице «Хранилища»",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["EbTextDimBrush"]
            });
        }
        else
        {
            foreach (var s in storages)
            {
                var cb = new CheckBox
                {
                    Content = DescribeStorage(s),
                    IsChecked = s.Kind == StorageKind.LocalFolder,
                    Tag = s
                };
                _storageChecks.Add(cb);
                StoragesPanel.Children.Add(cb);
            }
        }

        LaunchBtn.IsEnabled = MainWindow.Instance?.IsBusy != true;
    }

    internal static string DescribeStorage(SavedStorage s) => s.Kind switch
    {
        StorageKind.LocalFolder => $"{s.Name}  ({s.Path})",
        StorageKind.Sftp => $"{s.Name}  ({s.Username}@{s.Host}:{s.Port})",
        _ => s.Name
    };

    // ---------- свои папки (общий конфиг — расписания тоже его читают) ----------

    private static List<string> LoadFolders() => CustomFolderConfig.Load();

    private void SaveFolders() => CustomFolderConfig.Save(_folders);

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

        var targets = _storageChecks.Where(cb => cb.IsChecked == true)
            .Select(cb => (SavedStorage)cb.Tag)
            .ToList();

        string? err = null;
        if (modules.Count == 0)
            err = "Выбери хотя бы один модуль или добавь папку.";
        else if (targets.Count == 0)
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
            await MainWindow.Instance.StartBackupAsync(new BackupRequest(modules, targets, passphrase));
        }
        finally
        {
            LaunchBtn.IsEnabled = true;
        }
    }
}
