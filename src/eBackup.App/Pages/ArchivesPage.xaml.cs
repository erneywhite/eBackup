using eBackup.Core.Crypto;
using eBackup.Security;
using eBackup.Storage.Sftp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

public sealed partial class ArchivesPage : Page
{
    private readonly SftpConnectionStore _store = new(new DpapiSecretProtector());
    private bool _refreshing;

    public ArchivesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            // Обновляемся сами, когда бэкап завершился, пока страница открыта.
            MainWindow.BackupCompleted += OnBackupCompleted;
            await RefreshAsync();
        };
        Unloaded += (_, _) => MainWindow.BackupCompleted -= OnBackupCompleted;
    }

    private async void OnBackupCompleted() => await RefreshAsync();

    private static string LocalBackupDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "eBackup", "Backups");

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        RefreshBtn.IsEnabled = false;

        try
        {
            Sections.Children.Clear();

            // ---- локальные архивы
            var localDir = LocalBackupDir();
            AddHeader($"Локально — {localDir}");
            try
            {
                var files = Directory.Exists(localDir)
                    ? Directory.GetFiles(localDir, "*.ebk")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTime)
                        .ToList()
                    : [];

                if (files.Count == 0)
                {
                    AddDim("архивов пока нет — сделай первый бэкап кнопкой внизу");
                }
                else
                {
                    foreach (var f in files)
                    {
                        var encrypted = false;
                        try { encrypted = ArchiveCipher.IsEncrypted(f.FullName); } catch { }
                        AddRow(f.Name,
                            $"{f.Length / 1024.0 / 1024.0:0.#} МБ · {f.LastWriteTime:dd.MM.yyyy HH:mm}"
                            + (encrypted ? " · 🔒 зашифрован" : ""));
                    }
                }
            }
            catch (Exception ex)
            {
                AddDim("не удалось прочитать папку: " + ex.Message);
            }

            // ---- архивы на SFTP-серверах
            List<SavedSftpConnection> connections;
            try
            {
                connections = (await _store.LoadAsync()).ToList();
            }
            catch
            {
                connections = [];
            }

            foreach (var conn in connections)
            {
                AddHeader($"{conn.Name} — {conn.Username}@{conn.Host}:{conn.Port}, папка {conn.RemoteDirectory}");
                try
                {
                    var provider = new SftpStorageProvider(_store.Unprotect(conn));
                    var files = await provider.ListAsync();
                    if (files.Count == 0)
                        AddDim("архивов нет");
                    else
                        foreach (var f in files.OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase))
                            AddRow(f, "на сервере");
                }
                catch (Exception ex)
                {
                    AddDim("✕ сервер недоступен: " + ex.Message);
                }
            }
        }
        finally
        {
            _refreshing = false;
            RefreshBtn.IsEnabled = true;
        }
    }

    // ---------- строительные блоки списка ----------

    private void AddHeader(string text)
        => Sections.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["EbTextDimBrush"],
            Margin = new Thickness(2, 10, 0, 2),
            TextWrapping = TextWrapping.Wrap
        });

    private void AddDim(string text)
        => Sections.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["EbTextDimBrush"],
            Margin = new Thickness(14, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap
        });

    private void AddRow(string title, string subtitle)
    {
        var appRes = Application.Current.Resources;
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = (Brush)appRes["EbTextDimBrush"]
        });

        Sections.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = (Brush)appRes["EbCardBrush"],
            BorderBrush = (Brush)appRes["EbCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            Child = panel
        });
    }
}
