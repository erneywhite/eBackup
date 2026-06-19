using eBackup.Core.Scheduling;
using eBackup.Ipc.Client;
using eBackup.Ipc.Contracts;
using eBackup.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

/// <summary>Элемент списка расписаний (для x:Bind). Источник — служба (ScheduleDetail).</summary>
public sealed class ScheduleItem(ScheduleDetail detail)
{
    public ScheduleDetail Detail { get; } = detail;
    public string Title => Detail.Name + (Detail.Enabled ? "" : "  ⏸");

    public string Subtitle
    {
        get
        {
            var when = SchedulePage.Describe(Detail);
            if (!Detail.Enabled)
                return $"{when} · приостановлено";

            if (string.Equals(Detail.Kind, nameof(ScheduleKind.DailyWhenIdle), StringComparison.Ordinal))
            {
                var ranToday = Detail.LastRunAt is { } last && last.LocalDateTime.Date == DateTime.Now.Date;
                return $"{when} · {(ranToday ? "сегодня уже выполнен" : "ожидает простоя")}";
            }

            return Detail.NextRun is { } next ? $"{when} · следующий: {next.LocalDateTime:dd.MM HH:mm}" : when;
        }
    }
}

public sealed partial class SchedulePage : Page
{
    private static readonly string[] ShortDays = ["пн", "вт", "ср", "чт", "пт", "сб", "вс"];

    /// <summary>Индекс пн..вс → DayOfWeek (с воскресеньем в конце недели).</summary>
    private static DayOfWeek DayFromIndex(int i) => (DayOfWeek)((i + 1) % 7);
    private static int IndexFromDay(DayOfWeek d) => ((int)d + 6) % 7;

    private List<ScheduleDetail> _schedules = [];
    private ScheduleDetail? _editing;   // null — создаём новое
    private bool _suppressSelection;
    private readonly List<CheckBox> _moduleChecks = [];
    private readonly List<CheckBox> _storageChecks = [];
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

    private static ScheduleKind KindOf(ScheduleDetail s)
        => Enum.TryParse<ScheduleKind>(s.Kind, out var k) ? k : ScheduleKind.DailyWhenIdle;

    /// <summary>Человекочитаемое описание периодичности.</summary>
    public static string Describe(ScheduleDetail s) => KindOf(s) switch
    {
        ScheduleKind.Daily => $"ежедневно в {s.Hour:00}:{s.Minute:00}",
        ScheduleKind.Weekly =>
            $"еженедельно: {string.Join(", ", s.Days.OrderBy(d => IndexFromDay((DayOfWeek)d)).Select(d => ShortDays[IndexFromDay((DayOfWeek)d)]))} в {s.Hour:00}:{s.Minute:00}",
        ScheduleKind.EveryHours => $"каждые {s.EveryHours} ч",
        _ => "раз в день, при простое ПК"
    };

    /// <summary>Подключение к службе для текущей операции (с заполнением статуса при ошибке).</summary>
    private async Task<IpcClient?> ClientOrStatusAsync()
    {
        var client = await ServiceConnection.GetClientAsync();
        if (client is null)
            SetStatus("✕ Служба eBackup недоступна: " + (ServiceConnection.Shared.Error ?? ""), ok: false);
        return client;
    }

    // ---------- список ----------

    private async Task ReloadAsync(string? selectId)
    {
        var client = await ServiceConnection.GetClientAsync();
        try
        {
            _schedules = client is null ? [] : (await client.ListSchedulesAsync()).ToList();
            if (client is null)
                SetStatus("✕ Служба eBackup недоступна: " + (ServiceConnection.Shared.Error ?? ""), ok: false);
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

        var toSelect = selectId is null ? null : items.FirstOrDefault(i => i.Detail.Id == selectId);
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

        _editing = item.Detail;
        await ShowEditorAsync(item.Detail);
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

    private async Task ShowEditorAsync(ScheduleDetail? s)
    {
        EmptyHintPanel.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
        EditorTitle.Text = s is null ? "Новое расписание" : s.Name;
        NameBox.Text = s?.Name ?? string.Empty;

        var client = await ServiceConnection.GetClientAsync();

        // Модули: все рабочие из реестра службы (свой набор расписания; глобальный выключатель не действует).
        ModulesPanel.Children.Clear();
        _moduleChecks.Clear();
        if (client is not null)
        {
            try
            {
                foreach (var m in (await client.ListModulesAsync()).Where(m => m.Problem is null))
                {
                    var cb = new CheckBox
                    {
                        Content = m.DisplayName,
                        Tag = m.Id,
                        IsChecked = s?.ModuleIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase) ?? true
                    };
                    _moduleChecks.Add(cb);
                    ModulesPanel.Children.Add(cb);
                }
            }
            catch { /* модули недоступны — можно бэкапить «свои папки» */ }
        }

        // Свои папки — собственный список расписания.
        _schedFolders = s?.CustomFolderIds.ToList() ?? [];
        RenderSchedFolders();

        // Цели: хранилища службы.
        StoragesPanel.Children.Clear();
        _storageChecks.Clear();
        var dim = (Brush)Application.Current.Resources["EbTextDimBrush"];
        List<SavedStorage> storages = [];
        if (client is not null)
        {
            try { storages = (await client.ListStorageDetailsAsync()).Select(ServiceStorage.ToSaved).ToList(); }
            catch { storages = []; }
        }

        if (storages.Count == 0)
        {
            StoragesPanel.Children.Add(new TextBlock
            {
                Text = client is null
                    ? "служба eBackup недоступна"
                    : "хранилищ нет — добавь на странице «Хранилища»",
                FontSize = 12,
                Foreground = dim
            });
        }
        else
        {
            foreach (var st in storages)
            {
                var isChecked = s is null
                    ? st.Kind == StorageKind.LocalFolder
                    : s.TargetStorageIds.Contains(st.Id, StringComparer.OrdinalIgnoreCase);
                var cb = new CheckBox
                {
                    Content = BackupPage.DescribeStorage(st),
                    Tag = st.Id,
                    IsChecked = isChecked
                };
                _storageChecks.Add(cb);
                StoragesPanel.Children.Add(cb);
            }
        }

        // Шифрование: фразу не показываем; пусто при редактировании = оставить прежнюю
        EncryptCheck.IsChecked = s?.HasPassphrase == true;
        Pass1.Password = string.Empty;
        Pass2.Password = string.Empty;
        Pass1.PlaceholderText = s?.HasPassphrase == true ? "пусто — оставить прежнюю" : "парольная фраза";
        Pass2.PlaceholderText = s?.HasPassphrase == true ? "пусто — оставить прежнюю" : "повтори фразу";

        // Когда («при простое» — рекомендуемый дефолт для нового)
        var kind = s is null ? ScheduleKind.DailyWhenIdle : KindOf(s);
        IdleRadio.IsChecked = kind == ScheduleKind.DailyWhenIdle;
        DailyRadio.IsChecked = kind == ScheduleKind.Daily;
        WeeklyRadio.IsChecked = kind == ScheduleKind.Weekly;
        EveryRadio.IsChecked = kind == ScheduleKind.EveryHours;
        TimeBox.SelectedTime = new TimeSpan(s?.Hour ?? 3, s?.Minute ?? 0, 0);
        var days = s?.Days.Select(d => (DayOfWeek)d).ToList() ?? [DayOfWeek.Monday];
        foreach (var cb in _dayChecks)
            cb.IsChecked = days.Contains(DayFromIndex((int)cb.Tag));
        EveryBox.Text = (s?.EveryHours ?? 6).ToString();
        IdleBox.Text = (s?.IdleMinutes ?? 5).ToString();

        EnabledToggle.IsOn = s?.Enabled ?? true;
        DeleteBtn.Visibility = s is null ? Visibility.Collapsed : Visibility.Visible;
        RunNowBtn.Visibility = s is null ? Visibility.Collapsed : Visibility.Visible;
        SetStatus(string.Empty, ok: true);
    }

    /// <summary>
    /// Ручной запуск — по СОХРАНЁННЫМ настройкам расписания (правки формы не участвуют). Идёт ЧЕРЕЗ СЛУЖБУ
    /// тем же путём, что и таймер: совпадает с плановым (свои сжатие/имя ПК/retention), а зашифрованное
    /// служба расшифрует машинным ключом сама (GUI фразы не видит). Ошибки приходят в нижнюю панель.
    /// </summary>
    private void RunNowBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null || MainWindow.Instance is null)
            return;
        if (MainWindow.Instance.IsBusy)
        {
            SetStatus("Уже идёт бэкап или восстановление — подожди завершения.", ok: false);
            return;
        }

        // Не ждём завершения: кнопке важен сам старт, прогресс — в нижней панели.
        _ = MainWindow.Instance.RunScheduleNowAsync(_editing.Id, _editing.Name);
        SetStatus("✓ Запущено — прогресс в нижней панели.", ok: true);
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

        var targetIds = _storageChecks.Where(cb => cb.IsChecked == true)
            .Select(cb => (string)cb.Tag).ToList();
        if (targetIds.Count == 0)
        {
            SetStatus("Выбери хотя бы одно хранилище.", ok: false);
            return;
        }

        // Парольная фраза: служба шифрует машинным ключом. null — оставить прежнюю; "" — снять; иначе — задать.
        string? newPassphrase;
        if (EncryptCheck.IsChecked == true)
        {
            if (Pass1.Password.Length > 0 || Pass2.Password.Length > 0)
            {
                if (Pass1.Password != Pass2.Password)
                {
                    SetStatus("Парольные фразы не совпадают.", ok: false);
                    return;
                }
                newPassphrase = Pass1.Password;
            }
            else if (_editing?.HasPassphrase == true)
            {
                newPassphrase = null; // оставить прежнюю
            }
            else
            {
                SetStatus("Введи парольную фразу для шифрования.", ok: false);
                return;
            }
        }
        else
        {
            newPassphrase = ""; // шифрование выключено
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

        // Параметры прогона: у службы нет настроек GUI, поэтому несём их в расписании.
        // При правке сохраняем прежние, для нового — берём текущие настройки приложения.
        var settings = AppSettings.Load();
        var input = new ScheduleInput
        {
            Id = _editing?.Id ?? Guid.NewGuid().ToString("N")[..8],
            Name = name,
            ModuleIds = moduleIds.ToArray(),
            CustomFolderIds = _schedFolders.ToArray(),
            TargetStorageIds = targetIds.ToArray(),
            Kind = kind.ToString(),
            Hour = time.Hours,
            Minute = time.Minutes,
            Days = days.Select(d => (int)d).ToArray(),
            EveryHours = everyHours,
            IdleMinutes = idleMinutes,
            Enabled = EnabledToggle.IsOn,
            CompressionMode = _editing?.CompressionMode ?? settings.CompressionMode,
            IncludeMachineName = _editing?.IncludeMachineName ?? settings.IncludeMachineNameInArchive,
            RetentionCount = _editing?.RetentionCount ?? (settings.RetentionCount > 0 ? settings.RetentionCount : null),
            NewPassphrase = newPassphrase,
        };

        var client = await ClientOrStatusAsync();
        if (client is null)
            return;

        try
        {
            await client.UpsertScheduleAsync(input);
            await ReloadAsync(selectId: input.Id);

            // Зашифрованные расписания служба выполняет автоматически (фразу хранит под машинным ключом).
            var encNote = EncryptCheck.IsChecked == true ? " · 🔒 шифрование включено" : "";
            SetStatus(!input.Enabled
                ? "✓ Сохранено (приостановлено)" + encNote
                : kind == ScheduleKind.DailyWhenIdle
                    ? "✓ Сохранено · выполнится при ближайшем простое ПК" + encNote
                    : "✓ Сохранено" + encNote, ok: true);
        }
        catch (IpcRequestException ex)
        {
            SetStatus("✕ " + ex.Error.Message, ok: false);
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

        var client = await ClientOrStatusAsync();
        if (client is null)
            return;

        try
        {
            await client.DeleteScheduleAsync(_editing.Id);
            _editing = null;
            await ReloadAsync(selectId: null);
        }
        catch (IpcRequestException ex)
        {
            SetStatus("✕ " + ex.Error.Message, ok: false);
        }
        catch (Exception ex)
        {
            SetStatus("✕ Не удалось удалить: " + ex.Message, ok: false);
        }
    }

    private void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)Application.Current.Resources[ok ? "EbOkBrush" : "EbErrBrush"];
    }
}
