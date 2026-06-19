using eBackup.Core.Abstractions;
using eBackup.Core.Modules;
using eBackup.Ipc.Client;
using eBackup.Modules.Obs;
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
        // Модули — включённые из реестра СЛУЖБЫ (это её модулями делается бэкап).
        ModulesPanel.Children.Clear();
        _moduleChecks.Clear();
        var moduleClient = await ServiceConnection.GetClientAsync();
        if (moduleClient is not null)
        {
            try
            {
                foreach (var m in (await moduleClient.ListModulesAsync()).Where(m => m.Problem is null && m.Enabled))
                {
                    var cb = new CheckBox { Content = m.DisplayName, IsChecked = false, Tag = m.Id };
                    _moduleChecks.Add(cb);
                    ModulesPanel.Children.Add(cb);
                }
            }
            catch { /* модули недоступны — можно бэкапить «свои папки» */ }
        }

        // Свои папки (сохраняются между сессиями)
        _folders = LoadFolders();
        RenderFolders();

        // Цели: хранилища СЛУЖБЫ (папки отмечены по умолчанию)
        StoragesPanel.Children.Clear();
        _storageChecks.Clear();
        var dimBrush = (Brush)Application.Current.Resources["EbTextDimBrush"];
        var client = await ServiceConnection.GetClientAsync();
        List<SavedStorage> storages;
        if (client is null)
        {
            storages = [];
            StoragesPanel.Children.Add(new TextBlock
            {
                Text = "служба eBackup недоступна: " + (ServiceConnection.Shared.Error ?? ""),
                FontSize = 12,
                Foreground = dimBrush
            });
        }
        else
        {
            try
            {
                storages = (await client.ListStorageDetailsAsync()).Select(ServiceStorage.ToSaved).ToList();
            }
            catch
            {
                storages = [];
            }
        }

        if (client is not null && storages.Count == 0)
        {
            StoragesPanel.Children.Add(new TextBlock
            {
                Text = "хранилищ нет — добавь на странице «Хранилища»",
                FontSize = 12,
                Foreground = dimBrush
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
        StorageKind.Sftp => $"{s.Name}  (sftp · {s.Username}@{s.Host}:{s.Port})",
        StorageKind.Ftp => $"{s.Name}  ({(s.UseFtps ? "ftps" : "ftp")} · {s.Username}@{s.Host}:{s.Port})",
        StorageKind.S3 => $"{s.Name}  (s3 · {s.Bucket})",
        StorageKind.WebDav => $"{s.Name}  (webdav · {s.ServiceUrl})",
        StorageKind.GoogleDrive => $"{s.Name}  (Google Drive)",
        StorageKind.Dropbox => $"{s.Name}  (Dropbox)",
        StorageKind.Mega => $"{s.Name}  (MEGA · {s.Username})",
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

        var moduleIds = _moduleChecks.Where(cb => cb.IsChecked == true)
            .Select(cb => (string)cb.Tag)
            .ToList();
        var folderPaths = _folders.ToList(); // все настроенные папки идут в бэкап
        var targetIds = _storageChecks.Where(cb => cb.IsChecked == true)
            .Select(cb => ((SavedStorage)cb.Tag).Id)
            .ToList();

        string? err = null;
        if (moduleIds.Count == 0 && folderPaths.Count == 0)
            err = "Выбери хотя бы один модуль или добавь папку.";
        else if (targetIds.Count == 0)
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
            await MainWindow.Instance.StartBackupAsync(new BackupRequest(moduleIds, folderPaths, targetIds, passphrase));
        }
        finally
        {
            LaunchBtn.IsEnabled = true;
        }
    }
}
