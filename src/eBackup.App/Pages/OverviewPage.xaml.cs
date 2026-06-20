using eBackup.Core.Scheduling;
using eBackup.Ipc.Client;
using eBackup.Ipc.Contracts;
using eBackup.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

/// <summary>Обзор-дашборд: последний бэкап, счётчики архивов/модулей/расписаний, статусы хранилищ.</summary>
public sealed partial class OverviewPage : Page
{
    private bool _refreshing;
    private int _refreshGen; // защита от «опоздавших» результатов прошлого обновления

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            MainWindow.BackupCompleted += OnBackupCompleted;
            Entrance.Play(ContentPanel); // карточки обзора всплывают по очереди при открытии
            await RefreshAsync();
        };
        Unloaded += (_, _) => MainWindow.BackupCompleted -= OnBackupCompleted;
    }

    private async void OnBackupCompleted() => await RefreshAsync();

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        RefreshBtn.IsEnabled = false;
        try
        {
            var dim = (Brush)Application.Current.Resources["EbTextDimBrush"];
            var settings = AppSettings.Load();

            // ---- модули (из реестра службы)
            var moduleClient = await ServiceConnection.GetClientAsync();
            if (moduleClient is null)
            {
                ModulesTileText.Text = Loc.Get("Overview_ServiceUnavailable");
            }
            else
            {
                try
                {
                    var mods = await moduleClient.ListModulesAsync();
                    var enabled = mods.Count(m => m.Problem is null && m.Enabled);
                    var paused = mods.Count(m => m.Problem is null && !m.Enabled);
                    var blocked = mods.Count(m => m.Problem is not null);
                    ModulesTileText.Text = Loc.Get("Overview_ModulesEnabled", enabled)
                        + (paused > 0 ? "\n" + Loc.Get("Overview_ModulesPaused", paused) : "")
                        + (blocked > 0 ? "\n" + Loc.Get("Overview_ModulesBlocked", blocked) : "");
                }
                catch
                {
                    ModulesTileText.Text = Loc.Get("Overview_ModulesReadFailed");
                }
            }

            // ---- расписания (из службы — их теперь исполняет служба, GUI лишь читает)
            if (moduleClient is null)
            {
                ScheduleTileText.Text = Loc.Get("Overview_ServiceUnavailable");
            }
            else
            {
                try
                {
                    var schedules = await moduleClient.ListSchedulesAsync();
                    var active = schedules.Where(x => x.Enabled).ToList();
                    if (active.Count == 0)
                    {
                        ScheduleTileText.Text = Loc.Get("Overview_ScheduleNone");
                    }
                    else
                    {
                        var now = DateTime.Now;
                        var nexts = active.Where(x => x.NextRun is not null)
                            .Select(x => x.NextRun!.Value.LocalDateTime)
                            .ToList();
                        var idlePending = active.Any(x =>
                            string.Equals(x.Kind, nameof(ScheduleKind.DailyWhenIdle), StringComparison.Ordinal)
                            && (x.LastRunAt is null || x.LastRunAt.Value.LocalDateTime.Date < now.Date));

                        ScheduleTileText.Text = Loc.Get("Overview_ScheduleActive", active.Count)
                            + (nexts.Count > 0 ? "\n" + Loc.Get("Overview_ScheduleNext", nexts.Min()) : "")
                            + (idlePending ? "\n" + Loc.Get("Overview_ScheduleWaitingIdle") : "");
                    }
                }
                catch
                {
                    ScheduleTileText.Text = Loc.Get("Overview_ScheduleReadFailed");
                }
            }

            // ---- хранилища: строки с бейджами + счётчики архивов (читаем из СЛУЖБЫ; листинг — через неё же)
            StorageRows.Children.Clear();
            var client = await ServiceConnection.GetClientAsync();
            if (client is null)
            {
                LastBackupTitle.Text = Loc.Get("Overview_ServiceUnavailable");
                LastBackupSub.Text = ServiceConnection.Shared.Error ?? Loc.Get("Overview_ServiceConnectFailed");
                ArchivesTileText.Text = Loc.Get("Overview_ServiceUnavailable");
                StorageRows.Children.Add(new TextBlock { Text = Loc.Get("Overview_ServiceUnavailableRow"), FontSize = 12, Foreground = dim });
                return;
            }

            List<SavedStorage> storages;
            try
            {
                storages = (await client.ListStorageDetailsAsync()).Select(ServiceStorage.ToSaved).ToList();
            }
            catch
            {
                storages = [];
            }

            if (storages.Count == 0)
            {
                LastBackupTitle.Text = Loc.Get("Overview_NeverRun");
                LastBackupSub.Text = Loc.Get("Overview_NoStorageHint");
                ArchivesTileText.Text = Loc.Get("Overview_NoStorage");
                StorageRows.Children.Add(new TextBlock
                {
                    Text = Loc.Get("Overview_NoStorageRow"),
                    FontSize = 12,
                    Foreground = dim
                });
                return;
            }

            LastBackupTitle.Text = "…";
            LastBackupSub.Text = Loc.Get("Overview_GatheringStorage");
            ArchivesTileText.Text = (settings.RetentionCount > 0
                ? Loc.Get("Overview_RetentionPrefix", settings.RetentionCount) + "\n" : "") + Loc.Get("Overview_Counting");

            var gen = ++_refreshGen;
            var checks = new List<Task<(SavedStorage Storage, IReadOnlyList<RemoteFileDto>? Files)>>();
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

                // Кол-во архивов и занятое место — заполняется по результату проверки.
                var stats = new TextBlock
                {
                    FontSize = 12,
                    Foreground = dim,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock
                {
                    Text = BackupPage.DescribeStorage(s),
                    FontSize = 12,
                    Foreground = dim,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(label, 0);
                Grid.SetColumn(stats, 1);
                Grid.SetColumn(badge, 2);
                row.Children.Add(label);
                row.Children.Add(stats);
                row.Children.Add(badge);
                StorageRows.Children.Add(row);

                checks.Add(CheckOneAsync(client, s, stats, badge));
            }

            _ = FinishStatsAsync(checks, settings, gen); // бейджи и счётчики — по мере прихода
        }
        finally
        {
            _refreshing = false;
            RefreshBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Одно хранилище = один запрос: листинг архивов и как проверка доступности (бейдж),
    /// и как источник счётчиков и занятого места. Никогда не бросает.
    /// </summary>
    private async Task<(SavedStorage Storage, IReadOnlyList<RemoteFileDto>? Files)> CheckOneAsync(
        IpcClient client, SavedStorage s, TextBlock stats, TextBlock badge)
    {
        try
        {
            IReadOnlyList<RemoteFileDto> files = await client.ListArchivesAsync(s.Id);
            badge.Text = "✓";
            badge.Foreground = (Brush)Application.Current.Resources["EbOkBrush"];
            stats.Text = files.Count == 0
                ? Loc.Get("Overview_Empty")
                : Loc.Get("Overview_ArchiveStats", files.Count, FormatSize(files.Sum(f => f.Length)));
            return (s, files);
        }
        catch
        {
            badge.Text = "✕";
            badge.Foreground = (Brush)Application.Current.Resources["EbErrBrush"];
            return (s, null);
        }
    }

    private static string FormatSize(long bytes)
        => bytes >= 1L << 30
            ? Loc.Get("Overview_SizeGb", bytes / 1024.0 / 1024 / 1024)
            : Loc.Get("Overview_SizeMb", bytes / 1024.0 / 1024);

    /// <summary>Когда все ответы собраны — плитка «Архивы» и герой «Последний бэкап».</summary>
    private async Task FinishStatsAsync(
        List<Task<(SavedStorage Storage, IReadOnlyList<RemoteFileDto>? Files)>> checks,
        AppSettings settings, int gen)
    {
        var results = await Task.WhenAll(checks);
        if (gen != _refreshGen)
            return; // страница успела обновиться заново — эти данные устарели

        var lines = results.Select(r => r.Files is null
            ? Loc.Get("Overview_StorageUnavailableLine", r.Storage.Name)
            : Loc.Get("Overview_StorageStatsLine", r.Storage.Name, r.Files.Count, r.Files.Sum(f => f.Length) / 1024.0 / 1024.0));
        ArchivesTileText.Text =
            (settings.RetentionCount > 0 ? Loc.Get("Overview_RetentionPrefix", settings.RetentionCount) + "\n" : "")
            + string.Join("\n", lines);

        // Последний бэкап — самый свежий архив по всем доступным хранилищам.
        var newest = results
            .Where(r => r.Files is not null)
            .SelectMany(r => r.Files!.Select(f => (r.Storage, File: f)))
            .OrderByDescending(x => x.File.LastWriteTime)
            .FirstOrDefault();

        if (newest.File is null)
        {
            LastBackupTitle.Text = Loc.Get("Overview_NeverRun");
            LastBackupSub.Text = Loc.Get("Overview_FirstBackupHint");
        }
        else
        {
            LastBackupTitle.Text = newest.File.Name;
            LastBackupSub.Text =
                Loc.Get("Overview_LastBackupSub", newest.File.LastWriteTime, newest.File.Length / 1024.0 / 1024.0, newest.Storage.Name);
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
