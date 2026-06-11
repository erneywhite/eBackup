using eBackup.Core.Modules;
using eBackup.Core.Scheduling;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage.Sftp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

/// <summary>Элемент списка расписаний (для x:Bind).</summary>
public sealed class ScheduleItem(BackupSchedule schedule)
{
    public BackupSchedule Schedule { get; } = schedule;
    public string Title => Schedule.Name + (Schedule.Enabled ? "" : "  ⏸");

    public string Subtitle
    {
        get
        {
            var when = SchedulePage.Describe(Schedule);
            if (!Schedule.Enabled)
                return $"{when} · приостановлено";

            if (Schedule.Kind == ScheduleKind.DailyWhenIdle)
            {
                var ranToday = Schedule.LastRunAt is { } last && last.Date == DateTime.Now.Date;
                return $"{when} · {(ranToday ? "сегодня уже выполнен" : "ожидает простоя")}";
            }

            var next = ScheduleTiming.NextRun(Schedule, DateTime.Now);
            return next is null ? when : $"{when} · следующий: {next:dd.MM HH:mm}";
        }
    }
}

public sealed partial class SchedulePage : Page
{
    private static readonly string[] ShortDays = ["пн", "вт", "ср", "чт", "пт", "сб", "вс"];

    /// <summary>Индекс пн..вс → DayOfWeek (с воскресеньем в конце недели).</summary>
    private static DayOfWeek DayFromIndex(int i) => (DayOfWeek)((i + 1) % 7);
    private static int IndexFromDay(DayOfWeek d) => ((int)d + 6) % 7;

    private readonly ScheduleStore _scheduleStore = new(new DpapiSecretProtector());
    private readonly SftpConnectionStore _connStore = new(new DpapiSecretProtector());
    private readonly ModuleRegistry _registry = new(
    [
        new BuiltInModuleSource([new ObsBackupModule()]),
        new DeclarativeModuleSource(),
    ]);

    private List<BackupSchedule> _schedules = [];
    private BackupSchedule? _editing;   // null — создаём новое
    private bool _suppressSelection;
    private readonly List<CheckBox> _moduleChecks = [];
    private readonly List<CheckBox> _sftpChecks = [];
    private List<string> _schedFolders = [];   // свои папки ЭТОГО расписания
    private readonly List<CheckBox> _dayChecks = [];

    public SchedulePage()
    {
        InitializeComponent();
        for (var i = 0; i < 7; i++)
        {
            var cb = new CheckBox
            {
                Content = ShortDays[i],
                Tag = i,
                MinWidth = 0,
                Padding = new Thickness(6, 4, 8, 4)
            };
            _dayChecks.Add(cb);
            DaysPanel.Children.Add(cb);
        }
        Loaded += async (_, _) => await ReloadAsync(selectId: null);
    }

    /// <summary>Человекочитаемое описание периодичности.</summary>
    public static string Describe(BackupSchedule s) => s.Kind switch
    {
        ScheduleKind.Daily => $"ежедневно в {s.Hour:00}:{s.Minute:00}",
        ScheduleKind.Weekly =>
            $"еженедельно: {string.Join(", ", s.Days.OrderBy(IndexFromDay).Select(d => ShortDays[IndexFromDay(d)]))} в {s.Hour:00}:{s.Minute:00}",
        ScheduleKind.EveryHours => $"каждые {s.EveryHours} ч",
        _ => "раз в день, при простое ПК"
    };

    // ---------- список ----------

    private async Task ReloadAsync(string? selectId)
    {
        try
        {
            _schedules = (await _scheduleStore.LoadAsync()).ToList();
        }
        catch (Exception ex)
        {
            _schedules = [];
            SetStatus("✕ Не удалось прочитать расписания: " + ex.Message, ok: false);
        }

        var items = _schedules.Select(s => new ScheduleItem(s)).ToList();
        _suppressSelection = true;
        ScheduleList.ItemsSource = items;
        _suppressSelection = false;

        var toSelect = selectId is null ? null : items.FirstOrDefault(i => i.Schedule.Id == selectId);
        if (toSelect is not null)
        {
            ScheduleList.SelectedItem = toSelect;
        }
        else
        {
            Editor.Visibility = Visibility.Collapsed;
            EmptyHintPanel.Visibility = Visibility.Visible;
        }
    }

    private async void ScheduleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || ScheduleList.SelectedItem is not ScheduleItem item)
            return;

        _editing = item.Schedule;
        await ShowEditorAsync(item.Schedule);
    }

    private async void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        _suppressSelection = true;
        ScheduleList.SelectedItem = null;
        _suppressSelection = false;

        _editing = null;
        await ShowEditorAsync(null);
    }

    // ---------- редактор ----------

    private async Task ShowEditorAsync(BackupSchedule? s)
    {
        EmptyHintPanel.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
        EditorTitle.Text = s is null ? "Новое расписание" : s.Name;
        NameBox.Text = s?.Name ?? string.Empty;

        // Модули (свой набор расписания; глобальный выключатель здесь не действует)
        ModulesPanel.Children.Clear();
        _moduleChecks.Clear();
        foreach (var d in _registry.Discover().Where(d => d.Problem is null && d.Instance is not null))
        {
            var cb = new CheckBox
            {
                Content = d.DisplayName,
                Tag = d.Id,
                IsChecked = s?.ModuleIds.Contains(d.Id, StringComparer.OrdinalIgnoreCase) ?? true
            };
            _moduleChecks.Add(cb);
            ModulesPanel.Children.Add(cb);
        }

        // Свои папки — собственный список расписания.
        _schedFolders = s?.CustomFolders.ToList() ?? [];
        RenderSchedFolders();

        // Цели
        LocalCheck.IsChecked = s?.KeepLocal ?? true;
        SftpPanel.Children.Clear();
        _sftpChecks.Clear();
        List<SavedSftpConnection> connections;
        try
        {
            connections = (await _connStore.LoadAsync()).ToList();
        }
        catch
        {
            connections = [];
        }
        foreach (var c in connections)
        {
            var cb = new CheckBox
            {
                Content = $"{c.Name}  ({c.Username}@{c.Host}:{c.Port})",
                Tag = c.Id,
                IsChecked = s?.TargetConnectionIds.Contains(c.Id, StringComparer.OrdinalIgnoreCase) ?? false
            };
            _sftpChecks.Add(cb);
            SftpPanel.Children.Add(cb);
        }

        // Шифрование: фразу не показываем; пусто при редактировании = оставить прежнюю
        EncryptCheck.IsChecked = s?.ProtectedPassphrase is not null;
        Pass1.Password = string.Empty;
        Pass2.Password = string.Empty;
        Pass1.PlaceholderText = s?.ProtectedPassphrase is null ? "парольная фраза" : "пусто — оставить прежнюю";
        Pass2.PlaceholderText = s?.ProtectedPassphrase is null ? "повтори фразу" : "пусто — оставить прежнюю";

        // Когда («при простое» — рекомендуемый дефолт для нового)
        IdleRadio.IsChecked = s is null || s.Kind == ScheduleKind.DailyWhenIdle;
        DailyRadio.IsChecked = s?.Kind == ScheduleKind.Daily;
        WeeklyRadio.IsChecked = s?.Kind == ScheduleKind.Weekly;
        EveryRadio.IsChecked = s?.Kind == ScheduleKind.EveryHours;
        TimeBox.SelectedTime = new TimeSpan(s?.Hour ?? 3, s?.Minute ?? 0, 0);
        var days = s?.Days ?? [DayOfWeek.Monday];
        foreach (var cb in _dayChecks)
            cb.IsChecked = days.Contains(DayFromIndex((int)cb.Tag));
        EveryBox.Text = (s?.EveryHours ?? 6).ToString();
        IdleBox.Text = (s?.IdleMinutes ?? 5).ToString();

        EnabledToggle.IsOn = s?.Enabled ?? true;
        DeleteBtn.Visibility = s is null ? Visibility.Collapsed : Visibility.Visible;
        SetStatus(string.Empty, ok: true);
    }

    private void EncryptCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (Pass1 is null || Pass2 is null)
            return;
        var on = EncryptCheck.IsChecked == true;
        Pass1.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Pass2.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Kind_Changed(object sender, RoutedEventArgs e)
    {
        if (TimeRow is null || DayRow is null || EveryRow is null || IdleRow is null)
            return;
        var strictTime = DailyRadio.IsChecked == true || WeeklyRadio.IsChecked == true;
        TimeRow.Visibility = strictTime ? Visibility.Visible : Visibility.Collapsed;
        DayRow.Visibility = WeeklyRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EveryRow.Visibility = EveryRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        IdleRow.Visibility = IdleRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- свои папки расписания ----------

    private void RenderSchedFolders()
    {
        SchedFoldersPanel.Children.Clear();
        var dim = (Brush)Application.Current.Resources["EbTextDimBrush"];

        if (_schedFolders.Count == 0)
        {
            SchedFoldersPanel.Children.Add(new TextBlock
            {
                Text = "пока пусто — список папок у каждого расписания свой",
                FontSize = 12,
                Foreground = dim
            });
            return;
        }

        foreach (var folder in _schedFolders)
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
                CornerRadius = new CornerRadius(8)
            };
            remove.Click += (_, _) =>
            {
                _schedFolders.Remove(folder);
                RenderSchedFolders();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);

            SchedFoldersPanel.Children.Add(row);
        }
    }

    private async void AddSchedFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null)
            return;

        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || _schedFolders.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
            return;

        _schedFolders.Add(folder.Path);
        RenderSchedFolders();
    }

    // ---------- сохранение / удаление ----------

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            SetStatus("Укажи название расписания.", ok: false);
            return;
        }

        var moduleIds = _moduleChecks.Where(cb => cb.IsChecked == true)
            .Select(cb => (string)cb.Tag).ToList();
        if (moduleIds.Count == 0 && _schedFolders.Count == 0)
        {
            SetStatus("Выбери хотя бы один модуль или добавь папку.", ok: false);
            return;
        }

        var keepLocal = LocalCheck.IsChecked == true;
        var targetIds = _sftpChecks.Where(cb => cb.IsChecked == true)
            .Select(cb => (string)cb.Tag).ToList();
        if (!keepLocal && targetIds.Count == 0)
        {
            SetStatus("Выбери хотя бы одно хранилище.", ok: false);
            return;
        }

        // Парольная фраза: новая, прежняя или отсутствие шифрования.
        string? protectedPassphrase = null;
        if (EncryptCheck.IsChecked == true)
        {
            if (Pass1.Password.Length > 0 || Pass2.Password.Length > 0)
            {
                if (Pass1.Password != Pass2.Password)
                {
                    SetStatus("Парольные фразы не совпадают.", ok: false);
                    return;
                }
                protectedPassphrase = _scheduleStore.ProtectPassphrase(Pass1.Password);
            }
            else if (_editing?.ProtectedPassphrase is not null)
            {
                protectedPassphrase = _editing.ProtectedPassphrase; // оставить прежнюю
            }
            else
            {
                SetStatus("Введи парольную фразу для шифрования.", ok: false);
                return;
            }
        }

        var kind = WeeklyRadio.IsChecked == true ? ScheduleKind.Weekly
                 : EveryRadio.IsChecked == true ? ScheduleKind.EveryHours
                 : DailyRadio.IsChecked == true ? ScheduleKind.Daily
                 : ScheduleKind.DailyWhenIdle;

        var everyHours = _editing?.EveryHours ?? 6;
        if (kind == ScheduleKind.EveryHours &&
            (!int.TryParse(EveryBox.Text.Trim(), out everyHours) || everyHours is < 1 or > 168))
        {
            SetStatus("«Каждые N часов» — число от 1 до 168.", ok: false);
            return;
        }

        var idleMinutes = _editing?.IdleMinutes ?? 5;
        if (kind == ScheduleKind.DailyWhenIdle &&
            (!int.TryParse(IdleBox.Text.Trim(), out idleMinutes) || idleMinutes is < 1 or > 240))
        {
            SetStatus("Минуты простоя — число от 1 до 240.", ok: false);
            return;
        }

        var time = TimeBox.SelectedTime ?? new TimeSpan(3, 0, 0);
        var days = _dayChecks.Where(cb => cb.IsChecked == true)
            .Select(cb => DayFromIndex((int)cb.Tag))
            .ToList();
        if (kind == ScheduleKind.Weekly && days.Count == 0)
        {
            SetStatus("Выбери хотя бы один день недели.", ok: false);
            return;
        }
        if (days.Count == 0)
            days = [DayOfWeek.Monday];

        var schedule = new BackupSchedule
        {
            Id = _editing?.Id ?? Guid.NewGuid().ToString("N")[..8],
            Name = name,
            ModuleIds = moduleIds,
            CustomFolders = _schedFolders.ToList(),
            KeepLocal = keepLocal,
            TargetConnectionIds = targetIds,
            ProtectedPassphrase = protectedPassphrase,
            Kind = kind,
            Hour = time.Hours,
            Minute = time.Minutes,
            Days = days,
            EveryHours = everyHours,
            IdleMinutes = idleMinutes,
            Enabled = EnabledToggle.IsOn,
            // Точные расписания стартуют «с этого момента» (без задним-числом);
            // «при простое» — без отметки, чтобы выполниться при первом же простое.
            LastRunAt = _editing?.LastRunAt
                ?? (kind == ScheduleKind.DailyWhenIdle ? null : DateTime.Now)
        };

        try
        {
            var all = _schedules.Where(s => s.Id != schedule.Id).Append(schedule).ToList();
            await _scheduleStore.SaveAllAsync(all);
            await ReloadAsync(selectId: schedule.Id);
            var next = ScheduleTiming.NextRun(schedule, DateTime.Now);
            SetStatus(!schedule.Enabled
                ? "✓ Сохранено (приостановлено)"
                : schedule.Kind == ScheduleKind.DailyWhenIdle
                    ? "✓ Сохранено · выполнится при ближайшем простое ПК"
                    : $"✓ Сохранено · следующий запуск: {next:dd.MM.yyyy HH:mm}", ok: true);
        }
        catch (Exception ex)
        {
            SetStatus("✕ Не удалось сохранить: " + ex.Message, ok: false);
        }
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null)
            return;

        var appRes = Application.Current.Resources;
        var dialog = new ContentDialog
        {
            Title = "Удалить расписание?",
            Content = $"«{_editing.Name}» больше не будет запускаться. Архивы не трогаем.",
            PrimaryButtonText = "Удалить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            Background = (Brush)appRes["EbDialogBrush"],
            BorderBrush = (Brush)appRes["EbCardBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            CloseButtonStyle = (Style)appRes["EbDialogCloseStyle"]
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            await _scheduleStore.SaveAllAsync(_schedules.Where(s => s.Id != _editing.Id));
            _editing = null;
            await ReloadAsync(selectId: null);
        }
        catch (Exception ex)
        {
            SetStatus("✕ Не удалось удалить: " + ex.Message, ok: false);
        }
    }

    private void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)Resources[ok ? "EbOkBrush" : "EbErrBrush"];
    }
}
