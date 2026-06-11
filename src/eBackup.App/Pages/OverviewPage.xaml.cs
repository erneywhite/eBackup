using eBackup.Core.Modules;
using eBackup.Core.Scheduling;
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
    private int _refreshGen;          // защита от «опоздавших» результатов прошлого обновления
    private string _archivesBaseText = string.Empty;

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
            _archivesBaseText = (archives.Length == 0
                    ? "локально пока пусто"
                    : $"локально: {archives.Length} шт · {totalMb:0.#} МБ")
                + (settings.RetentionCount > 0 ? $"\nхранится последних: {settings.RetentionCount}" : "");
            ArchivesTileText.Text = _archivesBaseText;

            // ---- модули
            var descriptors = _registry.Discover();
            var enabled = descriptors.Count(d => d.Problem is null && d.Enabled);
            var paused = descriptors.Count(d => d.Problem is null && !d.Enabled);
            var blocked = descriptors.Count(d => d.Problem is not null);
            ModulesTileText.Text = $"✓ включено: {enabled}"
                + (paused > 0 ? $"\n⏸ выключено: {paused}" : "")
                + (blocked > 0 ? $"\n✕ заблокировано: {blocked}" : "");

            // ---- расписания
            try
            {
                var schedules = await new ScheduleStore(new DpapiSecretProtector()).LoadAsync();
                var active = schedules.Where(x => x.Enabled).ToList();
                if (active.Count == 0)
                {
                    ScheduleTileText.Text = "нет активных — создай в «Расписании»";
                }
                else
                {
                    var now = DateTime.Now;
                    var nexts = active.Select(x => ScheduleTiming.NextRun(x, now))
                        .Where(n => n is not null)
                        .Select(n => n!.Value)
                        .ToList();
                    var idlePending = active.Any(x => x.Kind == ScheduleKind.DailyWhenIdle
                        && (x.LastRunAt is null || x.LastRunAt.Value.Date < now.Date));

                    ScheduleTileText.Text = $"активных: {active.Count}"
                        + (nexts.Count > 0 ? $"\nближайший: {nexts.Min():dd.MM HH:mm}" : "")
                        + (idlePending ? "\nожидает простоя ПК" : "");
                }
            }
            catch
            {
                ScheduleTileText.Text = "не удалось прочитать расписания";
            }

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
                ArchivesTileText.Text = _archivesBaseText + "\nна серверах: считаю…";
                var gen = ++_refreshGen;
                var checks = new List<Task<(string Name, IReadOnlyList<RemoteFileInfo>? Files)>>();
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

                    checks.Add(CheckOneAsync(conn, badge));
                }

                _ = FinishRemoteCountsAsync(checks, gen); // бейджи и счётчики — по мере прихода
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// Одно подключение = один запрос: листинг архивов и как проверка доступности (бейдж),
    /// и как источник счётчика для плитки «Архивы». Никогда не бросает.
    /// </summary>
    private async Task<(string Name, IReadOnlyList<RemoteFileInfo>? Files)> CheckOneAsync(
        SavedSftpConnection conn, TextBlock badge)
    {
        try
        {
            var files = await new SftpStorageProvider(_store.Unprotect(conn)).ListDetailedAsync();
            badge.Text = "✓";
            badge.Foreground = (Brush)Resources["EbOkBrush"];
            return (conn.Name, files);
        }
        catch
        {
            badge.Text = "✕";
            badge.Foreground = (Brush)Resources["EbErrBrush"];
            return (conn.Name, null);
        }
    }

    /// <summary>Дописывает серверные счётчики в плитку «Архивы», когда все ответы собраны.</summary>
    private async Task FinishRemoteCountsAsync(
        List<Task<(string Name, IReadOnlyList<RemoteFileInfo>? Files)>> checks, int gen)
    {
        var results = await Task.WhenAll(checks);
        if (gen != _refreshGen)
            return; // страница успела обновиться заново — эти данные устарели

        var lines = results.Select(r => r.Files is null
            ? $"{r.Name}: недоступен"
            : $"{r.Name}: {r.Files.Count} шт · {r.Files.Sum(f => f.Length) / 1024.0 / 1024.0:0.#} МБ");
        ArchivesTileText.Text = _archivesBaseText + "\n" + string.Join("\n", lines);
    }

    // ---------- навигация с плиток ----------

    private void GoArchives_Click(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.SelectNav("archives");

    private void GoModules_Click(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.SelectNav("modules");

    private void GoStorage_Click(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.SelectNav("storage");

    private void GoSchedule_Click(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.SelectNav("schedule");
}
