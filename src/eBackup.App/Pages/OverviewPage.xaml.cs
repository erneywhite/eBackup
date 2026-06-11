using eBackup.Core.Modules;
using eBackup.Core.Scheduling;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage;
using eBackup.Storage.Sftp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

/// <summary>Обзор-дашборд: последний бэкап, счётчики архивов/модулей/расписаний, статусы хранилищ.</summary>
public sealed partial class OverviewPage : Page
{
    private readonly ModuleRegistry _registry = new(
    [
        new BuiltInModuleSource([new ObsBackupModule()]),
        new DeclarativeModuleSource(),
    ]);
    private readonly StorageStore _store = new(new DpapiSecretProtector());
    private bool _refreshing;
    private int _refreshGen; // защита от «опоздавших» результатов прошлого обновления

    public OverviewPage()
    {
        InitializeComponent();
        // Контент следует за шириной окна до потолка 860. На MaxWidth положиться
        // нельзя: ширину лево-выровненного StackPanel задаёт самый широкий ребёнок,
        // а все дети теперь тянущиеся.
        Scroller.SizeChanged += (_, e) =>
            ContentPanel.Width = Math.Clamp(e.NewSize.Width, 0, 860);
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

            // ---- хранилища: строки с бейджами + счётчики архивов (один запрос на хранилище)
            StorageRows.Children.Clear();
            List<SavedStorage> storages;
            try
            {
                storages = (await _store.LoadAsync()).ToList();
            }
            catch
            {
                storages = [];
            }

            if (storages.Count == 0)
            {
                LastBackupTitle.Text = "ещё не выполнялся";
                LastBackupSub.Text = "добавь хранилище в «Хранилищах» и нажми «Сделать бэкап»";
                ArchivesTileText.Text = "хранилищ нет";
                StorageRows.Children.Add(new TextBlock
                {
                    Text = "хранилищ нет — добавь в «Хранилищах»",
                    FontSize = 12,
                    Foreground = dim
                });
                return;
            }

            LastBackupTitle.Text = "…";
            LastBackupSub.Text = "собираю данные по хранилищам…";
            ArchivesTileText.Text = (settings.RetentionCount > 0
                ? $"хранится последних: {settings.RetentionCount}\n" : "") + "считаю…";

            var gen = ++_refreshGen;
            var checks = new List<Task<(SavedStorage Storage, IReadOnlyList<RemoteFileInfo>? Files)>>();
            foreach (var s in storages)
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
                    Text = BackupPage.DescribeStorage(s),
                    FontSize = 12,
                    Foreground = dim,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(label, 0);
                Grid.SetColumn(badge, 1);
                row.Children.Add(label);
                row.Children.Add(badge);
                StorageRows.Children.Add(row);

                checks.Add(CheckOneAsync(s, badge));
            }

            _ = FinishStatsAsync(checks, settings, gen); // бейджи и счётчики — по мере прихода
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// Одно хранилище = один запрос: листинг архивов и как проверка доступности (бейдж),
    /// и как источник счётчиков. Никогда не бросает.
    /// </summary>
    private async Task<(SavedStorage Storage, IReadOnlyList<RemoteFileInfo>? Files)> CheckOneAsync(
        SavedStorage s, TextBlock badge)
    {
        try
        {
            var files = await StorageFactory.Create(s, _store.Protector).ListDetailedAsync();
            badge.Text = "✓";
            badge.Foreground = (Brush)Application.Current.Resources["EbOkBrush"];
            return (s, files);
        }
        catch
        {
            badge.Text = "✕";
            badge.Foreground = (Brush)Application.Current.Resources["EbErrBrush"];
            return (s, null);
        }
    }

    /// <summary>Когда все ответы собраны — плитка «Архивы» и герой «Последний бэкап».</summary>
    private async Task FinishStatsAsync(
        List<Task<(SavedStorage Storage, IReadOnlyList<RemoteFileInfo>? Files)>> checks,
        AppSettings settings, int gen)
    {
        var results = await Task.WhenAll(checks);
        if (gen != _refreshGen)
            return; // страница успела обновиться заново — эти данные устарели

        var lines = results.Select(r => r.Files is null
            ? $"{r.Storage.Name}: недоступно"
            : $"{r.Storage.Name}: {r.Files.Count} шт · {r.Files.Sum(f => f.Length) / 1024.0 / 1024.0:0.#} МБ");
        ArchivesTileText.Text =
            (settings.RetentionCount > 0 ? $"хранится последних: {settings.RetentionCount}\n" : "")
            + string.Join("\n", lines);

        // Последний бэкап — самый свежий архив по всем доступным хранилищам.
        var newest = results
            .Where(r => r.Files is not null)
            .SelectMany(r => r.Files!.Select(f => (r.Storage, File: f)))
            .OrderByDescending(x => x.File.LastWriteTime)
            .FirstOrDefault();

        if (newest.File is null)
        {
            LastBackupTitle.Text = "ещё не выполнялся";
            LastBackupSub.Text = "нажми «Сделать бэкап» внизу, чтобы создать первый архив";
        }
        else
        {
            LastBackupTitle.Text = newest.File.Name;
            LastBackupSub.Text =
                $"{newest.File.LastWriteTime:dd.MM.yyyy HH:mm} · {newest.File.Length / 1024.0 / 1024.0:0.#} МБ · {newest.Storage.Name}";
        }
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
