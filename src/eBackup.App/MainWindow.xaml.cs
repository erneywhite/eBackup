using eBackup.App.Pages;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Core.Modules;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage.Sftp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App;

public sealed partial class MainWindow : Window
{
    private readonly ModuleRegistry _registry = new(
    [
        new BuiltInModuleSource([new ObsBackupModule()]),
        new DeclarativeModuleSource(),
    ]);
    private readonly SftpConnectionStore _store = new(new DpapiSecretProtector());
    private bool _backupRunning;

    public MainWindow()
    {
        InitializeComponent();
        Title = "eBackup";

        // Тёмный кастомный тайтлбар в стиле приложения.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragArea);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));

        ContentFrame.Navigate(typeof(OverviewPage));
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Событие может прилететь во время InitializeComponent, когда Frame ещё не создан.
        if (ContentFrame is null || Nav.SelectedItem is not ListViewItem { Tag: string tag })
            return;

        var page = tag switch
        {
            "modules" => typeof(ModulesPage),
            "storage" => typeof(StoragePage),
            "archives" => typeof(ArchivesPage),
            _ => typeof(OverviewPage)
        };

        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    // ---------- флоу бэкапа ----------

    private static string DefaultBackupDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "eBackup", "Backups");

    private async void BackupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_backupRunning)
            return;

        try
        {
            await RunBackupFlowAsync();
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Ошибка бэкапа";
            StatusSub.Text = ex.Message;
        }
        finally
        {
            _backupRunning = false;
            BackupBtn.IsEnabled = true;
            BackupProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RunBackupFlowAsync()
    {
        // --- данные для диалога: модули из реестра + сохранённые подключения
        var descriptors = _registry.Discover()
            .Where(d => d.Problem is null && d.Instance is not null)
            .ToList();

        List<SavedSftpConnection> connections;
        try
        {
            connections = (await _store.LoadAsync()).ToList();
        }
        catch
        {
            connections = [];
        }

        // --- собираем содержимое диалога
        var dim = (Brush)Application.Current.Resources["EbTextDimBrush"];

        var moduleChecks = descriptors
            .Select(d => new CheckBox { Content = d.DisplayName, IsChecked = true, Tag = d.Instance })
            .ToList();

        var localCheck = new CheckBox { Content = "Локальная папка (Документы\\eBackup\\Backups)", IsChecked = true };
        var sftpChecks = connections
            .Select(c => new CheckBox { Content = $"{c.Name}  ({c.Username}@{c.Host}:{c.Port})", Tag = c })
            .ToList();

        var encryptCheck = new CheckBox { Content = "Зашифровать архив (AES-256, ключ из парольной фразы)" };
        var pass1 = new PasswordBox { PlaceholderText = "парольная фраза", Visibility = Visibility.Collapsed };
        var pass2 = new PasswordBox { PlaceholderText = "повтори фразу", Visibility = Visibility.Collapsed };
        encryptCheck.Checked += (_, _) => { pass1.Visibility = Visibility.Visible; pass2.Visibility = Visibility.Visible; };
        encryptCheck.Unchecked += (_, _) => { pass1.Visibility = Visibility.Collapsed; pass2.Visibility = Visibility.Collapsed; };

        var errorText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0x8A, 0x9C))
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = "Что бэкапим", FontSize = 12, Foreground = dim });
        foreach (var cb in moduleChecks) panel.Children.Add(cb);
        panel.Children.Add(new TextBlock { Text = "Куда", FontSize = 12, Foreground = dim, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(localCheck);
        foreach (var cb in sftpChecks) panel.Children.Add(cb);
        panel.Children.Add(new TextBlock { Text = "Шифрование", FontSize = 12, Foreground = dim, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(encryptCheck);
        panel.Children.Add(pass1);
        panel.Children.Add(pass2);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Сделать бэкап",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Запустить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            string? err = null;
            if (!moduleChecks.Any(cb => cb.IsChecked == true))
                err = "Выбери хотя бы один модуль.";
            else if (localCheck.IsChecked != true && !sftpChecks.Any(cb => cb.IsChecked == true))
                err = "Выбери хотя бы одно хранилище.";
            else if (encryptCheck.IsChecked == true)
            {
                if (pass1.Password.Length == 0)
                    err = "Введи парольную фразу.";
                else if (pass1.Password != pass2.Password)
                    err = "Парольные фразы не совпадают.";
            }

            if (err is not null)
            {
                errorText.Text = err;
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        // --- выбранное
        var modules = moduleChecks.Where(cb => cb.IsChecked == true)
            .Select(cb => (IBackupModule)cb.Tag).ToList();
        var targets = sftpChecks.Where(cb => cb.IsChecked == true)
            .Select(cb => (SavedSftpConnection)cb.Tag).ToList();
        var keepLocal = localCheck.IsChecked == true;
        var passphrase = encryptCheck.IsChecked == true ? pass1.Password : null;

        // --- поехали
        _backupRunning = true;
        BackupBtn.IsEnabled = false;
        BackupProgress.Visibility = Visibility.Visible;
        StatusTitle.Text = "Делаю бэкап…";
        StatusSub.Text = "подготовка…";
        var progress = new Progress<string>(s => StatusSub.Text = s);

        // Если локально хранить не нужно — собираем во временной папке и удалим после заливки.
        var outDir = keepLocal
            ? DefaultBackupDir()
            : Path.Combine(Path.GetTempPath(), "eBackup");
        var name = $"ebackup-{DateTime.Now:yyyyMMdd-HHmmss}";

        var engine = new BackupEngine();
        var archive = await Task.Run(() => engine.CreateBackupAsync(modules, outDir, name, passphrase, progress));

        var uploaded = new List<string>();
        var failed = new List<string>();
        foreach (var conn in targets)
        {
            ((IProgress<string>)progress).Report($"Загружаю на {conn.Name}…");
            try
            {
                var provider = new SftpStorageProvider(_store.Unprotect(conn));
                await provider.UploadAsync(archive, Path.GetFileName(archive));
                uploaded.Add(conn.Name);
            }
            catch (Exception ex)
            {
                failed.Add($"{conn.Name}: {ex.Message}");
            }
        }

        if (!keepLocal)
        {
            try { File.Delete(archive); } catch { /* мусор в temp не критичен */ }
        }

        // --- итог
        var parts = new List<string>();
        if (keepLocal) parts.Add(archive);
        if (uploaded.Count > 0) parts.Add("→ " + string.Join(", ", uploaded));

        StatusTitle.Text = failed.Count == 0 ? "Готов к работе" : "Бэкап завершён с ошибками";
        StatusSub.Text = $"последний бэкап: {DateTime.Now:HH:mm}  {string.Join("  ", parts)}"
            + (failed.Count > 0 ? $"  ✕ {string.Join("; ", failed)}" : string.Empty);
    }
}
