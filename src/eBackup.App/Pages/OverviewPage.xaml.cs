using eBackup.Core.Modules;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage.Sftp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

/// <summary>Обзор-дашборд: последний бэкап, счётчики архивов/модулей, статусы хранилищ.</summary>
public sealed partial class OverviewPage : Page
{
    private readonly ModuleRegistry _registry = new(
    [
        new BuiltInModuleSource([new ObsBackupModule()]),
        new DeclarativeModuleSource(),
    ]);
    private readonly SftpConnectionStore _store = new(new DpapiSecretProtector());
    private bool _refreshing;

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            MainWindow.BackupCompleted += OnBackupCompleted;
            await RefreshAsync();
        };
        Unloaded += (_, _) => MainWindow.BackupCompleted -= OnBackupCompleted;
    }

    private async void OnBackupCompleted() => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            var dim = (Brush)Application.Current.Resources["EbTextDimBrush"];
            var settings = AppSettings.Load();

            // ---- последний бэкап + счётчик архивов (локальная папка)
            FileInfo[] archives = [];
            try
            {
                if (Directory.Exists(settings.LocalBackupDir))
                    archives = Directory.GetFiles(settings.LocalBackupDir, "*.ebk")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTime)
                        .ToArray();
            }
            catch
            {
                // папка недоступна — покажем «нет архивов»
            }

            var newest = archives.FirstOrDefault();
            if (newest is null)
            {
                LastBackupTitle.Text = "ещё не выполнялся";
                LastBackupSub.Text = "нажми «Сделать бэкап» внизу, чтобы создать первый архив";
            }
            else
            {
                LastBackupTitle.Text = newest.Name;
                LastBackupSub.Text =
                    $"{newest.LastWriteTime:dd.MM.yyyy HH:mm} · {newest.Length / 1024.0 / 1024.0:0.#} МБ · {settings.LocalBackupDir}";
            }

            var totalMb = archives.Sum(f => f.Length) / 1024.0 / 1024.0;
            ArchivesTileText.Text = archives.Length == 0
                ? "локально пока пусто"
                : $"{archives.Length} шт · {totalMb:0.#} МБ локально"
                  + (settings.RetentionCount > 0 ? $"\nхранится последних: {settings.RetentionCount}" : "");

            // ---- модули
            var descriptors = _registry.Discover();
            var enabled = descriptors.Count(d => d.Problem is null && d.Enabled);
            var paused = descriptors.Count(d => d.Problem is null && !d.Enabled);
            var blocked = descriptors.Count(d => d.Problem is not null);
            ModulesTileText.Text = $"✓ включено: {enabled}"
                + (paused > 0 ? $"\n⏸ выключено: {paused}" : "")
                + (blocked > 0 ? $"\n✕ заблокировано: {blocked}" : "");

            // ---- хранилища: строки + живой селф-тест
            StorageRows.Children.Clear();
            List<SavedSftpConnection> connections;
            try
            {
                connections = (await _store.LoadAsync()).ToList();
            }
            catch
            {
                connections = [];
            }

            if (connections.Count == 0)
            {
                StorageRows.Children.Add(new TextBlock
                {
                    Text = "подключений нет — добавь в «Хранилищах»",
                    FontSize = 12,
                    Foreground = dim
                });
            }
            else
            {
                var checks = new List<Task>();
                foreach (var conn in connections)
                {
                    var badge = new TextBlock
                    {
                        Text = "…",
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = dim,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var label = new TextBlock
                    {
                        Text = $"{conn.Name}  ({conn.Username}@{conn.Host}:{conn.Port})",
                        FontSize = 12,
                        Foreground = dim,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    Grid.SetColumn(label, 0);
                    Grid.SetColumn(badge, 1);
                    row.Children.Add(label);
                    row.Children.Add(badge);
                    StorageRows.Children.Add(row);

                    checks.Add(TestOneAsync(conn, badge));
                }

                _ = Task.WhenAll(checks); // бейджи обновятся по мере прихода результатов
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Проверка одного подключения; результат — в бейдж. Никогда не бросает.</summary>
    private async Task TestOneAsync(SavedSftpConnection conn, TextBlock badge)
    {
        try
        {
            var result = await new SftpStorageProvider(_store.Unprotect(conn)).TestConnectionAsync();
            badge.Text = result.Success ? "✓" : "✕";
            badge.Foreground = (Brush)Resources[result.Success ? "EbOkBrush" : "EbErrBrush"];
        }
        catch
        {
            badge.Text = "✕";
            badge.Foreground = (Brush)Resources["EbErrBrush"];
        }
    }

    // ---------- навигация с плиток ----------

    private void GoArchives_Click(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.SelectNav("archives");

    private void GoModules_Click(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.SelectNav("modules");

    private void GoStorage_Click(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.SelectNav("storage");
}
