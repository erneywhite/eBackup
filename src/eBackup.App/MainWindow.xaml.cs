using System.Text.Json;
using eBackup.App.Pages;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Core.History;
using eBackup.Core.Modules;
using eBackup.Core.Scheduling;
using eBackup.Ipc.Client;
using eBackup.Ipc.Contracts;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace eBackup.App;

/// <summary>Параметры запуска бэкапа (id'ы — служба резолвит их в своём конфиге под машинным ключом).</summary>
public sealed record BackupRequest(
    IReadOnlyList<string> ModuleIds,
    IReadOnlyList<string> FolderPaths,
    IReadOnlyList<string> TargetStorageIds,
    string? Passphrase);

/// <summary>Параметры восстановления, собранные страницей «Восстановление».</summary>
public sealed record RestoreRequest(
    Pages.RestoreSource Source,
    IReadOnlyList<IBackupModule> Modules,
    Core.Engine.ConflictPolicy Policy,
    string? TargetDir,          // null — в исходные места
    string AssetsDir,
    string? Passphrase);

public sealed partial class MainWindow : Window
{
    /// <summary>Текущее окно — для страниц (запуск бэкапа, HWND для пикеров).</summary>
    public static MainWindow? Instance { get; private set; }

    /// <summary>Срабатывает по завершении бэкапа — страницы (напр. «Архивы») обновляются сами.</summary>
    public static event Action? BackupCompleted;

    private readonly StorageStore _storages = new(new DpapiSecretProtector());
    private bool _operationRunning; // бэкап или восстановление — одновременно только одно
    private CancellationTokenSource? _backupCts; // отмена текущего бэкапа (null — бэкап не идёт)
    private double _fill;           // текущая доля заливки-прогресса нижней панели (0..1)
    private DispatcherTimer? _scheduleTimer;
    private bool _checkingSchedules;
    // Кэш расписаний: файл перечитывается только при изменении (минутный тик
    // стоит один stat метаданных + арифметику дат — фактически бесплатно).
    private List<BackupSchedule>? _cachedSchedules;
    private DateTime _schedulesStamp;

    public bool IsBusy => _operationRunning;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        Title = "eBackup";

        // Тёмный кастомный тайтлбар в стиле приложения.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragArea);

        // Иконка окна (таскбар / Alt+Tab): задаём явно. WinUI 3 не берёт её из exe
        // надёжно — после обновления версии Windows иначе показывает старую из кэша.
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));

        // Минимальный размер окна: уже — и master-detail-страницы разваливаются.
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 940;
            presenter.PreferredMinimumHeight = 600;
        }

        InitLiquidProgress();

        // Обновления (GitHub Releases): баннер следит за общим состоянием,
        // тихая проверка раз в сутки при старте.
        UpdateService.Changed += OnUpdateStateChanged;
        _ = CheckForUpdatesAsync();

        // Хвосты браузера архивов (browse-*): при закрытии окна WinUI не гарантирует
        // Unloaded страницы, так что подчищаем при старте — свежий процесс ничего
        // из этого не держит. Занятые файлы (вторая копия приложения) пропускаются.
        _ = Task.Run(() =>
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "eBackup");
                if (!Directory.Exists(dir))
                    return;
                foreach (var file in Directory.EnumerateFiles(dir, "browse-*"))
                    try { File.Delete(file); } catch { }
            }
            catch { }
        });

        ContentFrame.Navigate(typeof(OverviewPage));

        // Одноразово: хранилище «Локальная папка» по умолчанию.
        _ = EnsureDefaultStorageAsync();

        // Проверка расписаний: раз в минуту + сразу после запуска (догон пропущенных).
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _scheduleTimer.Tick += async (_, _) => await CheckSchedulesAsync();
        _scheduleTimer.Start();
        _ = CheckSchedulesAsync();

        // Трей: клик — показать окно; закрытие окна — спрятаться в трей (настраивается).
        TrayIcon.LeftClickCommand = new RelayCommand(ShowFromTray);
        TrayIcon.ForceCreate();
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) => TrayIcon.Dispose();

        // Глобальная «назад»: кнопка + Alt+←/BrowserBack + боковая кнопка мыши (XButton1).
        ContentFrame.Navigated += OnFrameNavigated;
        RootGrid.AddHandler(UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnRootPointerPressed),
            handledEventsToo: true);

        // Клавиша Browser Back (VK_BROWSER_BACK = 166): в enum VirtualKey имени нет,
        // поэтому только из кода через числовой код.
        var browserBack = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)166
        };
        browserBack.Invoked += BackAccel_Invoked;
        RootGrid.KeyboardAccelerators.Add(browserBack);
    }

    // ---------- глобальная «назад» ----------

    private bool _syncingNav;

    private bool TryGoBack()
    {
        if (!ContentFrame.CanGoBack)
            return false;
        ContentFrame.GoBack();
        return true;
    }

    private void BackAccel_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        => args.Handled = TryGoBack();

    private void OnRootPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(RootGrid).Properties;
        if (props.IsXButton1Pressed && TryGoBack())
            e.Handled = true;
    }

    private void OnFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        // Кнопки «назад» в UI нет (решение пользователя) — только хоткеи:
        // Alt+←, клавиша Browser Back и боковая кнопка мыши (XButton1).

        // Подсветка таба следует за фактической страницей (важно при переходах «назад»).
        var tag = e.SourcePageType == typeof(OverviewPage) ? "overview"
                : e.SourcePageType == typeof(ModulesPage) ? "modules"
                : e.SourcePageType == typeof(StoragePage) ? "storage"
                : e.SourcePageType == typeof(ArchivesPage) ? "archives"
                : e.SourcePageType == typeof(SchedulePage) ? "schedule"
                : e.SourcePageType == typeof(HistoryPage) ? "history"
                : null;

        _syncingNav = true;
        try
        {
            if (tag is null)
            {
                Nav.SelectedItem = null;
            }
            else
            {
                foreach (var item in Nav.Items.OfType<ListViewItem>())
                {
                    if (item.Tag as string == tag)
                    {
                        Nav.SelectedItem = item;
                        break;
                    }
                }
            }
        }
        finally
        {
            _syncingNav = false;
        }

        AnimatePageEntrance(e.SourcePageType);
    }

    // ---------- направленный слайд между вкладками ----------

    private int _navIndex; // позиция текущей вкладки в сайдбаре

    private static int NavIndexOf(Type page)
        => page == typeof(OverviewPage) ? 0
         : page == typeof(ModulesPage) ? 1
         : page == typeof(StoragePage) ? 2
         : page == typeof(ArchivesPage) ? 3
         : page == typeof(SchedulePage) ? 4
         : page == typeof(HistoryPage) ? 5
         : page == typeof(SettingsPage) ? 6
         : -1; // Бэкап/Восстановление и пр. — вне списка вкладок

    /// <summary>
    /// Контент въезжает с той стороны, куда движемся по списку вкладок:
    /// вниз по списку — снизу, вверх — сверху. Страницы вне списка — снизу.
    /// </summary>
    private void AnimatePageEntrance(Type pageType)
    {
        var index = NavIndexOf(pageType);
        var fromBelow = index < 0 || index >= _navIndex;
        if (index >= 0)
            _navIndex = index;

        if (ContentFrame.Content is not UIElement page)
            return;

        var transform = new Microsoft.UI.Xaml.Media.TranslateTransform { Y = fromBelow ? 34 : -34 };
        page.RenderTransform = transform;
        page.Opacity = 0;

        var slide = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(240))
        };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(slide);
        sb.Children.Add(fade);
        sb.Begin();
    }

    /// <summary>При первом запуске создаёт хранилище «Локальная папка» (один раз).</summary>
    private async Task EnsureDefaultStorageAsync()
    {
        try
        {
            var settings = AppSettings.Load();
            if (settings.DefaultStorageCreated)
                return;

            var list = (await _storages.LoadAsync()).ToList();
            if (!list.Any(s => s.Kind == StorageKind.LocalFolder))
            {
                list.Add(new SavedStorage
                {
                    Id = "local",
                    Name = "Локальная папка",
                    Kind = StorageKind.LocalFolder,
                    Path = settings.LocalBackupDir
                });
                await _storages.SaveAllAsync(list);
            }

            // Свежая установка: встроенный модуль OBS выключен по умолчанию — база не
            // навязывает модули; нужный пользователь включит его тумблером в «Модулях».
            new eBackup.Core.Modules.ModuleRegistry([]).SetEnabled("obs", false);

            settings.DefaultStorageCreated = true;
            settings.Save();
        }
        catch
        {
            // не критично — пользователь добавит хранилище руками
        }
    }

    // ---------- трей ----------

    private bool _reallyExit;

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (!_reallyExit && AppSettings.Load().MinimizeToTray)
        {
            args.Cancel = true;
            sender.Hide(); // окно прячется, расписания продолжают работать
        }
    }

    /// <summary>Открыть локальный .ebk в браузере архива (ассоциация файлов).</summary>
    public void OpenLocalArchive(string path)
        => ContentFrame.Navigate(typeof(Pages.ArchiveBrowsePage),
            new Pages.RestoreSource(path, null, null));

    /// <summary>Показать окно из трея.</summary>
    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    /// <summary>Старт сразу в трей (автозапуск с «--minimized»).</summary>
    public void StartHidden() => AppWindow.Hide();

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    private void TrayBackup_Click(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        Nav.SelectedItem = null;
        if (ContentFrame.CurrentSourcePageType != typeof(BackupPage))
            ContentFrame.Navigate(typeof(BackupPage));
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;
        Close();
    }

    // ---------- навигация ----------

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Событие может прилететь во время InitializeComponent (Frame ещё не создан)
        // или при программной синхронизации подсветки после «назад».
        if (_syncingNav || ContentFrame is null || Nav.SelectedItem is not ListViewItem { Tag: string tag })
            return;

        var page = tag switch
        {
            "modules" => typeof(ModulesPage),
            "storage" => typeof(StoragePage),
            "archives" => typeof(ArchivesPage),
            "schedule" => typeof(SchedulePage),
            "history" => typeof(HistoryPage),
            _ => typeof(OverviewPage)
        };

        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    private void BackupBtn_Click(object sender, RoutedEventArgs e)
    {
        // Во время бэкапа та же кнопка работает на отмену.
        if (_backupCts is { IsCancellationRequested: false })
        {
            _backupCts.Cancel();
            BackupBtnText.Text = "Отменяю…";
            BackupBtn.IsEnabled = false;
            StatusSub.Text = "отмена…";
            return;
        }

        // Настройка бэкапа — обычная страница интерфейса, а не модальный диалог.
        Nav.SelectedItem = null;
        if (ContentFrame.CurrentSourcePageType != typeof(BackupPage))
            ContentFrame.Navigate(typeof(BackupPage));
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        Nav.SelectedItem = null;
        if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
            ContentFrame.Navigate(typeof(SettingsPage));
    }

    /// <summary>Перейти на страницу из навигации с подсветкой её таба (для плиток дашборда).</summary>
    public void SelectNav(string tag)
    {
        foreach (var item in Nav.Items.OfType<ListViewItem>())
        {
            if (item.Tag as string == tag)
            {
                Nav.SelectedItem = item; // SelectionChanged сам выполнит навигацию
                return;
            }
        }
    }

    // ---------- запуск бэкапа (страница «Бэкап» или расписание) ----------

    public async Task StartBackupAsync(BackupRequest request, string trigger = "вручную")
    {
        if (_operationRunning || request.TargetStorageIds.Count == 0)
            return;

        _operationRunning = true;
        _backupCts = new CancellationTokenSource();
        var ct = _backupCts.Token;
        var cancelled = false;
        BackupBtnText.Text = "Отменить"; // во время бэкапа кнопка отменяет операцию
        StatusTitle.Text = "Делаю бэкап…";
        StatusSub.Text = "подготовка…";
        SetFill(0.04);

        // Бэкап выполняет СЛУЖБА (под SYSTEM): окно ставит задачу и показывает живой прогресс из нот.
        // Историю пишет служба; «Обзор» подтянет результат через неё. Шифрование архива через службу
        // пока не подключено (S7) — при включённом шифровании честно отказываемся, чтобы не отдать открытый.
        string? jobId = null;
        var ok = false;
        string? error = null;
        var summary = "";
        try
        {
            if (request.Passphrase is not null)
                throw new InvalidOperationException(
                    "Шифрование архива через службу появится позже — пока сними галочку шифрования.");

            var client = await ServiceConnection.GetClientAsync(ct)
                ?? throw new InvalidOperationException(ServiceConnection.Shared.Error ?? "Служба eBackup недоступна.");

            // Регистрируем выбранные «свои папки» — служба бэкапит только зарегистрированные (не сырой путь с провода).
            foreach (var folder in request.FolderPaths)
                await client.UpsertCustomFolderAsync(folder, ct);

            var settings = AppSettings.Load();
            var resp = await client.StartBackupAsync(new StartBackupRequest
            {
                ModuleIds = request.ModuleIds.ToArray(),
                CustomFolderIds = request.FolderPaths.ToArray(),
                TargetStorageIds = request.TargetStorageIds.ToArray(),
                CompressionMode = settings.CompressionMode,
                IncludeMachineName = settings.IncludeMachineNameInArchive,
                RetentionCount = settings.RetentionCount > 0 ? settings.RetentionCount : null,
                Trigger = trigger,
                ClientRequestId = Guid.NewGuid().ToString("N"),
            }, ct);
            jobId = resp.JobId;

            // Живой прогресс из нот службы (Phase двигает «воду», Log — подпись) до терминальной ноты.
            await foreach (var f in client.AttachToJobAsync(jobId, 0, ct))
            {
                if (f.Op == IpcNotes.Phase && f.Body is { } pb
                    && pb.Deserialize(IpcJsonContext.Default.PhaseNote) is { } pn)
                {
                    StatusSub.Text = pn.Phase;
                    if (pn.ProgressFraction > 0) SetFill(0.05 + 0.9 * Math.Clamp(pn.ProgressFraction, 0, 1));
                    else BumpFill(stageEnd: 0.9);
                }
                else if (f.Op == IpcNotes.Log && f.Body is { } lb
                    && lb.Deserialize(IpcJsonContext.Default.LogNote) is { } ln)
                {
                    StatusSub.Text = ln.Text;
                }
            }

            // Поток окончился терминальной нотой — забираем финальный статус задачи.
            var job = await client.GetJobAsync(new GetJobRequest { JobId = jobId }, ct);
            ok = job.State == "Completed";
            error = job.Error;
            summary = job.ArchiveName is { } an
                ? $"{an} · {job.SizeBytes / 1024.0 / 1024.0:0.#} МБ"
                : (error ?? "бэкап завершён с ошибками");
            SetFill(1.0);
            StatusTitle.Text = ok ? "Готов к работе" : "Бэкап завершён с ошибками";
            StatusSub.Text = ok
                ? $"последний бэкап: {DateTime.Now:HH:mm}"
                    + (job.SkippedFiles > 0 ? $"  ⚠ пропущено файлов: {job.SkippedFiles}" : "")
                : $"✕ {summary}";
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            if (jobId is not null)
                try { await CancelJobViaServiceAsync(jobId); } catch { /* best-effort: соединение/токен уже закрыты */ }
            StatusTitle.Text = "Бэкап отменён";
            StatusSub.Text = "отменено пользователем";
        }
        catch (Exception ex)
        {
            error = ex.Message;
            StatusTitle.Text = "Ошибка бэкапа";
            StatusSub.Text = ex.Message;
        }
        finally
        {
            // Итог — системным уведомлением (при отмене пользователем не уведомляем).
            if (AppSettings.Load().NotifyOnBackgroundBackup && !cancelled)
                TryNotify(ok,
                    ok ? "✅ Бэкап выполнен" : "❌ Бэкап завершён с ошибками",
                    ok ? summary : (error ?? "подробности — на странице «История»"));

            _operationRunning = false;
            _backupCts?.Dispose();
            _backupCts = null;
            BackupBtnText.Text = "Сделать бэкап";
            BackupBtn.IsEnabled = true;
            BackupCompleted?.Invoke();
            await FadeOutFillAsync();
        }
    }

    /// <summary>Отменить задачу в службе (токен бэкапа уже отменён — берём свежее подключение).</summary>
    private static async Task CancelJobViaServiceAsync(string jobId)
    {
        var client = await ServiceConnection.GetClientAsync();
        if (client is not null)
            await client.CancelJobAsync(jobId);
    }


    // ---------- обновления (GitHub Releases, в стиле eCoda) ----------

    private async Task CheckForUpdatesAsync()
    {
        var settings = AppSettings.Load();
        // Проверяем при каждом запуске, но не чаще раза в сутки (чтобы не дёргать API).
        if (settings.LastUpdateCheck is { } last && (DateTimeOffset.Now - last).TotalHours < 24)
            return;

        await UpdateService.CheckAsync(quiet: true);

        var fresh = AppSettings.Load();
        fresh.LastUpdateCheck = DateTimeOffset.Now;
        fresh.Save();
    }

    /// <summary>Баннер обновления реагирует на общее состояние UpdateService.</summary>
    private void OnUpdateStateChanged(UpdateState s)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Уведомление об обновлении живёт в нижней панели (центр уведомлений) и ведёт в
            // «Настройки», где есть «Скачать и установить». Показываем только при наличии
            // новой версии; прогресс установки отображается уже на странице настроек.
            var available = s.Stage == UpdateStage.Available
                && s.Version != AppSettings.Load().DismissedUpdateVersion;
            UpdateHintBtn.Content = available
                ? $"🆕 Доступна версия {s.Version} — открыть «Настройки»"
                : string.Empty;
            UpdateHintBtn.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void UpdateHint_Click(object sender, RoutedEventArgs e)
    {
        // Подсказка об обновлении в нижней панели ведёт в «Настройки → Обновления».
        Nav.SelectedItem = null;
        if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
            ContentFrame.Navigate(typeof(SettingsPage));
    }

    /// <summary>Скачать установщик и запустить его с перезапуском (зовётся баннером и настройками).</summary>
    public async Task StartUpdateInstallAsync()
    {
        if (UpdateService.Current.Stage != UpdateStage.Available)
            return;
        var path = await UpdateService.DownloadAsync();
        if (path is not null && UpdateService.LaunchInstaller())
            QuitForUpdate();
    }

    /// <summary>Чистый выход перед запуском установщика (чтобы он заменил файлы).</summary>
    private void QuitForUpdate()
    {
        _reallyExit = true;
        Close();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1024.0 / 1024 / 1024:0.##} ГБ",
        >= 1L << 20 => $"{bytes / 1024.0 / 1024:0.#} МБ",
        _ => $"{Math.Max(1, bytes / 1024)} КБ"
    };

    /// <summary>
    /// Системное уведомление из трея. Цветной статус — эмодзи в заголовке
    /// (✅/❌ рендерятся цветными), у ошибок дополнительно системный красный крест.
    /// Сбой уведомления — не повод ронять бэкап.
    /// </summary>
    private void TryNotify(bool success, string title, string message)
    {
        try
        {
            TrayIcon.ShowNotification(title, message, success
                ? H.NotifyIcon.Core.NotificationIcon.None
                : H.NotifyIcon.Core.NotificationIcon.Error);
        }
        catch
        {
        }
    }

    // ---------- расписания (работают, пока приложение запущено) ----------

    private async Task CheckSchedulesAsync()
    {
        if (_checkingSchedules || _operationRunning)
            return;

        _checkingSchedules = true;
        try
        {
            var store = new ScheduleStore(new DpapiSecretProtector());

            // Перечитываем файл только если он менялся (правки на странице расписаний
            // или отметка LastRunAt после запуска тоже меняют файл — кэш сам обновится).
            DateTime stamp;
            try
            {
                var path = ScheduleStore.DefaultFilePath();
                stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch
            {
                stamp = DateTime.MinValue;
            }

            if (_cachedSchedules is null || stamp != _schedulesStamp)
            {
                try
                {
                    _cachedSchedules = (await store.LoadAsync()).ToList();
                    _schedulesStamp = stamp;
                }
                catch
                {
                    return; // битый файл расписаний не должен ронять приложение
                }
            }

            var schedules = _cachedSchedules;
            if (schedules.Count == 0)
                return;

            var now = DateTime.Now;
            var idle = IdleDetector.GetIdleTime();
            var due = schedules.FirstOrDefault(s => ScheduleTiming.IsDue(s, now, idle));
            if (due is null)
                return;

            // «При простое» = не только нет ввода, но и система свободна: бэкап не должен
            // стартовать поверх тяжёлой компиляции/рендера. Занято — попробуем через минуту.
            if (due.Kind == ScheduleKind.DailyWhenIdle)
            {
                var load = await SystemLoadMonitor.SampleAsync(TimeSpan.FromSeconds(1));
                if (!load.IsCalm())
                    return;
            }

            // Отмечаем запуск ДО выполнения: упавший бэкап не будет лупиться каждую минуту.
            await store.SaveAllAsync(schedules.Select(s => s.Id == due.Id ? s with { LastRunAt = now } : s));

            var request = await BuildRequestFromScheduleAsync(due, store);
            if (request is null)
            {
                StatusTitle.Text = "Расписание пропущено";
                StatusSub.Text = $"«{due.Name}»: нет модулей/целей или не расшифровалась парольная фраза";
                return;
            }

            StatusSub.Text = $"по расписанию «{due.Name}»…";
            await StartBackupAsync(request, trigger: $"расписание «{due.Name}»");
        }
        finally
        {
            _checkingSchedules = false;
        }
    }

    /// <summary>
    /// Запустить расписание немедленно (кнопка «Выполнить сейчас»).
    /// Возвращает null при успешном старте, иначе — текст причины.
    /// </summary>
    public async Task<string?> RunScheduleNowAsync(BackupSchedule schedule)
    {
        if (_operationRunning)
            return "Уже идёт бэкап или восстановление — подожди завершения.";

        var store = new ScheduleStore(new DpapiSecretProtector());
        var request = await BuildRequestFromScheduleAsync(schedule, store);
        if (request is null)
            return "Нет модулей/целей, или парольная фраза не расшифровалась.";

        // Ручной запуск занимает сегодняшний слот так же, как плановый:
        // дневное расписание после него не сработает повторно.
        try
        {
            var all = (await store.LoadAsync()).ToList();
            await store.SaveAllAsync(all.Select(s =>
                s.Id == schedule.Id ? s with { LastRunAt = DateTime.Now } : s));
        }
        catch { /* не критично: значит, плановый запуск просто случится в своё время */ }

        StatusSub.Text = $"вручную по расписанию «{schedule.Name}»…";
        // Не ждём завершения: кнопке важен сам старт.
        _ = StartBackupAsync(request, trigger: $"вручную · расписание «{schedule.Name}»");
        return null;
    }

    /// <summary>Собрать параметры бэкапа из расписания (id'ы — служба резолвит их в своём конфиге).</summary>
    private async Task<BackupRequest?> BuildRequestFromScheduleAsync(BackupSchedule s, ScheduleStore store)
    {
        var moduleIds = s.ModuleIds.ToList();
        var folderPaths = s.CustomFolders.ToList(); // свои папки у каждого расписания свои
        if (moduleIds.Count == 0 && folderPaths.Count == 0)
            return null;

        var targetIds = s.TargetConnectionIds.ToList();
        if (s.KeepLocal && !targetIds.Contains("local", StringComparer.OrdinalIgnoreCase))
            targetIds.Add("local"); // legacy: «локальная папка» отдельным флагом
        if (targetIds.Count == 0)
            return null;

        string? passphrase = null;
        if (s.ProtectedPassphrase is not null)
        {
            try
            {
                passphrase = store.UnprotectPassphrase(s.ProtectedPassphrase);
            }
            catch
            {
                return null; // фраза не расшифровалась (конфиг с другого ПК) — не бэкапим в открытую
            }
        }

        await Task.CompletedTask; // сигнатура async ради совместимости с вызовами; резолв теперь в службе
        return new BackupRequest(moduleIds, folderPaths, targetIds, passphrase);
    }

    // ---------- восстановление (вызывается страницей «Восстановление») ----------

    public async Task StartRestoreAsync(RestoreRequest request)
    {
        if (_operationRunning)
            return;

        _operationRunning = true;
        BackupBtn.IsEnabled = false;
        StatusTitle.Text = "Восстанавливаю…";
        StatusSub.Text = "подготовка…";
        SetFill(0.04);

        // Журнал «История»: восстановления пишутся так же подробно, как бэкапы.
        var history = new HistoryStore();
        var targetLabel = request.TargetDir is null ? "исходные места" : request.TargetDir;
        var run = new BackupRunRecord
        {
            Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}",
            Operation = "восстановление",
            StartedAt = DateTimeOffset.Now,
            Trigger = "вручную",
            Targets = [targetLabel]
        };
        void Log(string message) => history.AppendLog(run.Id, message);
        Log($"Восстановление → {targetLabel} · режим конфликтов: {request.Policy}");
        await history.SaveRunAsync(run);

        string? tempDownloaded = null;
        try
        {
            // Источник: прямой путь (хранилище-папка) либо скачивание из хранилища в temp.
            string archive;
            if (request.Source.LocalPath is not null)
            {
                archive = request.Source.LocalPath;
                Log($"Источник: {archive}");
            }
            else
            {
                // Архив в хранилище службы: тянем его ЧЕРЕЗ службу (seek-сессия) во временный файл —
                // полному восстановлению (хуки модулей, ассеты, расшифровка) нужен локальный файл.
                var client = await ServiceConnection.GetClientAsync()
                    ?? throw new InvalidOperationException(ServiceConnection.Shared.Error ?? "Служба eBackup недоступна.");
                StatusSub.Text = $"Получаю {request.Source.RemoteName} из службы…";
                Log($"Источник: хранилище «{request.Source.StorageId}» / {request.Source.RemoteName} — тяну через службу…");
                var open = await client.OpenArchiveReadAsync(request.Source.StorageId!, request.Source.RemoteName!);
                var handle = open.Handle;
                tempDownloaded = Path.Combine(Path.GetTempPath(), "eBackup", "restore",
                    $"{Guid.NewGuid():N}-{request.Source.RemoteName}");
                Directory.CreateDirectory(Path.GetDirectoryName(tempDownloaded)!);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                using (var remote = new RangeStream(open.Length,
                           (off, cnt, c) => client.ReadArchiveChunkAsync(handle, off, cnt, c),
                           onDispose: () => { _ = client.CloseArchiveReadAsync(handle); }))
                using (var fs = File.Create(tempDownloaded))
                    await remote.CopyToAsync(fs);
                archive = tempDownloaded;
                var seconds = Math.Max(0.1, watch.Elapsed.TotalSeconds);
                var size = new FileInfo(archive).Length;
                Log($"Получено: {size / 1024.0 / 1024.0:0.#} МБ за {seconds:0.#} с ({size / 1024.0 / 1024.0 / seconds:0.#} МБ/с)");
                SetFill(0.25);
            }

            run.ArchiveName = Path.GetFileName(archive);
            try { run.SizeBytes = new FileInfo(archive).Length; } catch { }

            if (Core.Crypto.ArchiveCipher.IsEncrypted(archive) && string.IsNullOrEmpty(request.Passphrase))
                throw new InvalidOperationException("Архив зашифрован — укажи парольную фразу.");

            var progress = new Progress<string>(s =>
            {
                StatusSub.Text = s;
                BumpFill(stageEnd: 0.95);
            });

            var engine = new BackupEngine();
            await Task.Run(() => engine.RestoreAsync(
                archive,
                request.Modules,
                request.Policy,
                destinationRootOverride: request.TargetDir,
                assetsDirectory: request.AssetsDir,
                passphrase: request.Passphrase,
                progress: progress,
                log: Log));

            run.Success = true;
            SetFill(1.0);
            StatusTitle.Text = "Готов к работе";
            StatusSub.Text = $"восстановлено {DateTime.Now:HH:mm}: {Path.GetFileName(archive)} → "
                + (request.TargetDir is null ? "исходные места" : request.TargetDir);
        }
        catch (Exception ex)
        {
            run.Success = false;
            run.Error = ex.Message;
            Log("✕ Ошибка: " + ex.Message);
            StatusTitle.Text = "Ошибка восстановления";
            StatusSub.Text = ex.Message;
        }
        finally
        {
            if (tempDownloaded is not null)
            {
                try { File.Delete(tempDownloaded); } catch { /* temp */ }
            }
            run.FinishedAt = DateTimeOffset.Now;
            var elapsed = run.FinishedAt.Value - run.StartedAt;
            Log(run.Success == true
                ? $"Готово за {elapsed:mm\\:ss}."
                : $"Завершено с ошибкой за {elapsed:mm\\:ss}.");
            await history.SaveRunAsync(run);

            _operationRunning = false;
            BackupBtn.IsEnabled = true;
            await FadeOutFillAsync();
        }
    }

    // ---------- «жидкая» заливка-прогресс нижней панели (вода с физикой) ----------

    // Вся вода — на правом крае-фронте. Мениск — пружинная модель ВДОЛЬ ВЫСОТЫ
    // панели: узел = горизонтальное отклонение фронта на своей высоте; узлы тянутся
    // к ровной кромке, обмениваются энергией с соседями, трение гасит. Внутри
    // жидкости всплывают пузырьки (подъёмная сила + покачивание).
    private double[] _waveH = [];   // горизонтальное отклонение фронта, px
    private double[] _waveV = [];   // скорость узла
    private double _fillCur;        // текущая ширина заливки, px (догоняет цель плавно)
    private double _simTime;
    private bool _simRunning;
    private readonly List<Bubble> _bubbles = [];
    private readonly Random _rng = new();
    // Шлейф: задний слой повторяет кромку переднего с задержкой в несколько кадров.
    private readonly Queue<double[]> _edgeHistory = new();
    private const int EdgeLagFrames = 7;
    private const double NodeStep = 6;       // шаг узлов мениска по вертикали, px
    private const double Spring = 0.024;     // жёсткость пружины к ровной кромке
    private const double Damping = 0.955;    // трение (выше — колебания живут дольше)
    private const double Spread = 0.11;      // передача энергии соседям (меньше — рябь локальнее)
    private const double MaxSwing = 20;      // предел колебания мениска, px

    private sealed class Bubble
    {
        public double BaseX, Y, Vy, Phase, Size;
        public required Microsoft.UI.Xaml.Shapes.Ellipse Visual;
    }

    /// <summary>Скруглённый Composition-клип: вода не выпирает из углов панели.</summary>
    private void InitLiquidProgress()
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(LiquidFill);
        var compositor = visual.Compositor;
        var clipGeometry = compositor.CreateRoundedRectangleGeometry();
        clipGeometry.CornerRadius = new System.Numerics.Vector2(15f, 15f);
        var sizeExpr = compositor.CreateExpressionAnimation("host.Size");
        sizeExpr.SetReferenceParameter("host", visual);
        clipGeometry.StartAnimation("Size", sizeExpr);
        visual.Clip = compositor.CreateGeometricClip(clipGeometry);
    }

    // Доступ страниц к заливке (браузер архива и др. операции вне Start*Async).
    public void ProgressStart(double fraction) => SetFill(fraction);
    public void ProgressBump(double stageEnd) => BumpFill(stageEnd);

    public async Task ProgressFinishAsync(bool complete = true)
    {
        if (complete)
            SetFill(1.0);
        await FadeOutFillAsync();
    }

    /// <summary>Довести воду до доли <paramref name="fraction"/> (0..1) — дольётся с всплеском.</summary>
    private void SetFill(double fraction)
    {
        _fill = Math.Clamp(fraction, 0, 1);
        LiquidFill.Opacity = 1;
        if (!_simRunning)
        {
            _simRunning = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnLiquidFrame;
        }
    }

    /// <summary>Небольшой сдвиг заливки вперёд внутри длинной фазы (асимптотически к её концу).</summary>
    private void BumpFill(double stageEnd)
        => SetFill(Math.Min(stageEnd, _fill + (stageEnd - _fill) * 0.18));

    private async Task FadeOutFillAsync()
    {
        await Task.Delay(1200);
        LiquidFill.Opacity = 0;
        _fill = 0;
        _fillCur = 0;
        Array.Clear(_waveH);
        Array.Clear(_waveV);
        _edgeHistory.Clear();
        _bubbles.Clear();
        BubblesLayer.Children.Clear();
        if (_simRunning)
        {
            _simRunning = false;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnLiquidFrame;
        }
        WaterFront.Data = null;
        WaterBack.Data = null;
    }

    private void OnLiquidFrame(object? sender, object e)
    {
        var width = BarRoot.ActualWidth;
        var height = BarRoot.ActualHeight;
        if (width < 40 || height < 20)
            return;

        var nodes = (int)Math.Ceiling(height / NodeStep) + 1;
        if (_waveH.Length != nodes)
        {
            _waveH = new double[nodes];
            _waveV = new double[nodes];
            _edgeHistory.Clear();
        }

        // Заливка догоняет цель; при доливе мениск получает толчок,
        // а вся кромка по инерции подаётся вперёд.
        var target = _fill * width;
        var advance = (target - _fillCur) * 0.055;
        _fillCur += advance;
        if (advance > 0.05)
        {
            var node = _rng.Next(0, nodes);
            _waveV[node] += advance * 1.0;
            if (node > 0) _waveV[node - 1] += advance * 0.5;
            if (node + 1 < nodes) _waveV[node + 1] += advance * 0.5;
            for (var i = 0; i < nodes; i++)
                _waveV[i] += advance * 0.06; // инерция всей массы
        }

        // Лёгкое фоновое колыхание + случайные микро-толчки: рябь без хорового движения.
        _simTime += 1 / 60.0;
        for (var i = 0; i < nodes; i++)
            _waveV[i] += 0.020 * Math.Sin(_simTime * 2.1 + i * 0.9)
                       + 0.014 * Math.Sin(_simTime * 1.3 - i * 0.6);
        if (_rng.NextDouble() < 0.30)
            _waveV[_rng.Next(nodes)] += (_rng.NextDouble() - 0.5) * 0.8;

        // Пружины + трение.
        for (var i = 0; i < nodes; i++)
        {
            _waveV[i] += -_waveH[i] * Spring;
            _waveV[i] *= Damping;
            _waveH[i] += _waveV[i];
        }

        // Волны разбегаются по мениску к соседям (один проход: сильнее
        // сглаживать нельзя — кромка начинает ходить хором, как натянутая плёнка).
        for (var i = 0; i < nodes; i++)
        {
            if (i > 0)
            {
                var d = (_waveH[i] - _waveH[i - 1]) * Spread;
                _waveV[i - 1] += d;
                _waveH[i - 1] += d * 0.5;
            }
            if (i < nodes - 1)
            {
                var d = (_waveH[i] - _waveH[i + 1]) * Spread;
                _waveV[i + 1] += d;
                _waveH[i + 1] += d * 0.5;
            }
        }

        for (var i = 0; i < nodes; i++)
            _waveH[i] = Math.Clamp(_waveH[i], -MaxSwing, MaxSwing);

        // Итоговое смещение кромки = пружины + бегущая рябь (две волны навстречу).
        var edge = new double[nodes];
        for (var i = 0; i < nodes; i++)
            edge[i] = _waveH[i]
                    + 3.2 * Math.Sin(_simTime * 1.7 + i * 0.65)
                    + 2.1 * Math.Sin(-_simTime * 1.15 + i * 1.25);

        // Передний слой — текущая кромка; задний — она же несколько кадров назад
        // (шлейф: догоняет переднюю, а не живёт своей жизнью).
        _edgeHistory.Enqueue(edge);
        var lagged = _edgeHistory.Count > EdgeLagFrames ? _edgeHistory.Dequeue() : edge;
        WaterFront.Data = BuildWater(0, 1.0, height, edge);
        WaterBack.Data = BuildWater(3, 0.92, height, lagged);

        // Пузырьки рождаются всегда, при активном доливе — охотнее.
        if (_bubbles.Count < 12 && _fillCur > 40 &&
            _rng.NextDouble() < 0.06 + Math.Min(advance, 3) * 0.12)
            SpawnBubble(height);
        UpdateBubbles(height);
    }

    /// <summary>
    /// Заливка во всю высоту от левого края до фронта-мениска по готовым смещениям
    /// кромки. Кромка рисуется гладкими кривыми через середины отрезков.
    /// </summary>
    private Microsoft.UI.Xaml.Media.PathGeometry? BuildWater(
        double xOffset, double scale, double height, double[] edge)
    {
        if (_fillCur < 4)
            return null;

        var n = edge.Length;
        var front = _fillCur + xOffset;
        var points = new Windows.Foundation.Point[n];
        for (var i = 0; i < n; i++)
            points[i] = new Windows.Foundation.Point(
                Math.Max(0, front + edge[i] * scale),
                Math.Min(i * NodeStep, height));

        var figure = new Microsoft.UI.Xaml.Media.PathFigure
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = points[0] });
        for (var i = 1; i < n - 1; i++)
            figure.Segments.Add(new Microsoft.UI.Xaml.Media.QuadraticBezierSegment
            {
                Point1 = points[i],
                Point2 = new Windows.Foundation.Point(
                    (points[i].X + points[i + 1].X) / 2,
                    (points[i].Y + points[i + 1].Y) / 2)
            });
        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = points[n - 1] });
        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment
        {
            Point = new Windows.Foundation.Point(0, height)
        });

        var geometry = new Microsoft.UI.Xaml.Media.PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>Пузырёк рождается у дна внутри жидкости и всплывает, покачиваясь.</summary>
    private void SpawnBubble(double height)
    {
        var size = 2.5 + _rng.NextDouble() * 3.5;
        var visual = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            Opacity = 0
        };
        var bubble = new Bubble
        {
            // Внутри заливки, ближе к фронту — там жидкость «свежая» и бурлит.
            BaseX = Math.Max(14, _fillCur * (0.45 + _rng.NextDouble() * 0.45)),
            Y = height - 6,
            Vy = -(0.25 + _rng.NextDouble() * 0.45),
            Phase = _rng.NextDouble() * Math.Tau,
            Size = size,
            Visual = visual
        };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(visual, bubble.BaseX);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(visual, bubble.Y);
        BubblesLayer.Children.Add(visual);
        _bubbles.Add(bubble);
    }

    private void UpdateBubbles(double height)
    {
        for (var i = _bubbles.Count - 1; i >= 0; i--)
        {
            var b = _bubbles[i];
            b.Y += b.Vy;
            b.Vy = Math.Max(b.Vy - 0.006, -1.0); // подъёмная сила разгоняет, но не бесконечно
            var x = b.BaseX + 2.2 * Math.Sin(_simTime * 3.0 + b.Phase);

            // Лопается у поверхности; исчезает, если фронт ушёл левее (вода «утекла»).
            if (b.Y <= 5 || x >= _fillCur - b.Size - 2)
            {
                BubblesLayer.Children.Remove(b.Visual);
                _bubbles.RemoveAt(i);
                continue;
            }

            // Плавное появление у дна и растворение у поверхности.
            var fadeIn = Math.Clamp((height - 6 - b.Y) / 10.0, 0, 1);
            var fadeOut = Math.Clamp((b.Y - 5) / 14.0, 0, 1);
            b.Visual.Opacity = 0.38 * fadeIn * fadeOut;
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(b.Visual, x);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(b.Visual, b.Y);
        }
    }
}
