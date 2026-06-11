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
            var next = ScheduleTiming.NextRun(Schedule, DateTime.Now);
            return next is null
                ? $"{when} · приостановлено"
                : $"{when} · следующий: {next:dd.MM HH:mm}";
        }
    }
}

public sealed partial class SchedulePage : Page
{
    private static readonly string[] DayNames =
        ["понедельник", "вторник", "среда", "четверг", "пятница", "суббота", "воскресенье"];

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

    public SchedulePage()
    {
        InitializeComponent();
        foreach (var d in DayNames)
            DayCombo.Items.Add(d);
        DayCombo.SelectedIndex = 0;
        Loaded += async (_, _) => await ReloadAsync(selectId: null);
    }

    /// <summary>Человекочитаемое описание периодичности.</summary>
    public static string Describe(BackupSchedule s) => s.Kind switch
    {
        ScheduleKind.Daily => $"ежедневно в {s.Hour:00}:{s.Minute:00}",
        ScheduleKind.Weekly => $"еженедельно, {DayNames[((int)s.Day + 6) % 7]} {s.Hour:00}:{s.Minute:00}",
        _ => $"каждые {s.EveryHours} ч"
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

        var foldersCount = CustomFolderConfig.Load().Count;
        FoldersCheck.Content = $"Свои папки ({foldersCount} шт, как на странице «Бэкап»)";
        FoldersCheck.IsChecked = s?.IncludeCustomFolders ?? foldersCount > 0;

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

        // Когда
        DailyRadio.IsChecked = s is null || s.Kind == ScheduleKind.Daily;
        WeeklyRadio.IsChecked = s?.Kind == ScheduleKind.Weekly;
        EveryRadio.IsChecked = s?.Kind == ScheduleKind.EveryHours;
        TimeBox.SelectedTime = new TimeSpan(s?.Hour ?? 3, s?.Minute ?? 0, 0);
        DayCombo.SelectedIndex = s is null ? 0 : ((int)s.Day + 6) % 7;
        EveryBox.Text = (s?.EveryHours ?? 6).ToString();

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
        if (TimeRow is null || DayRow is null || EveryRow is null)
            return;
        TimeRow.Visibility = EveryRadio.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        DayRow.Visibility = WeeklyRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EveryRow.Visibility = EveryRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
        var includeFolders = FoldersCheck.IsChecked == true;
        if (moduleIds.Count == 0 && !includeFolders)
        {
            SetStatus("Выбери хотя бы один модуль или свои папки.", ok: false);
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
                 : ScheduleKind.Daily;

        var everyHours = _editing?.EveryHours ?? 6;
        if (kind == ScheduleKind.EveryHours &&
            (!int.TryParse(EveryBox.Text.Trim(), out everyHours) || everyHours is < 1 or > 168))
        {
            SetStatus("«Каждые N часов» — число от 1 до 168.", ok: false);
            return;
        }

        var time = TimeBox.SelectedTime ?? new TimeSpan(3, 0, 0);
        var day = (DayOfWeek)((DayCombo.SelectedIndex + 1) % 7);

        var schedule = new BackupSchedule
        {
            Id = _editing?.Id ?? Guid.NewGuid().ToString("N")[..8],
            Name = name,
            ModuleIds = moduleIds,
            IncludeCustomFolders = includeFolders,
            KeepLocal = keepLocal,
            TargetConnectionIds = targetIds,
            ProtectedPassphrase = protectedPassphrase,
            Kind = kind,
            Hour = time.Hours,
            Minute = time.Minutes,
            Day = day,
            EveryHours = everyHours,
            Enabled = EnabledToggle.IsOn,
            // Новое расписание стартует «с этого момента», а не задним числом.
            LastRunAt = _editing?.LastRunAt ?? DateTime.Now
        };

        try
        {
            var all = _schedules.Where(s => s.Id != schedule.Id).Append(schedule).ToList();
            await _scheduleStore.SaveAllAsync(all);
            await ReloadAsync(selectId: schedule.Id);
            var next = ScheduleTiming.NextRun(schedule, DateTime.Now);
            SetStatus(next is null
                ? "✓ Сохранено (приостановлено)"
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
