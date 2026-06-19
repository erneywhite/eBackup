using System.Text.Json;
using eBackup.App.Pages;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Core.History;
using eBackup.Core.Modules;
using eBackup.Ipc.Client;
using eBackup.Ipc.Contracts;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace eBackup.App;

/// <summary>Параметры запуска бэкапа (id'ы — служба резолвит их в своём конфиге под машинным ключом).
/// Compression/IncludeMachineName/Retention: null — взять текущие настройки приложения; иначе — это
/// значение (расписание несёт свои, чтобы ручной запуск совпадал с плановым).</summary>
public sealed record BackupRequest(
    IReadOnlyList<string> ModuleIds,
    IReadOnlyList<string> FolderPaths,
    IReadOnlyList<string> TargetStorageIds,
    string? Passphrase,
    int? CompressionMode = null,
    bool? IncludeMachineName = null,
    int? RetentionCount = null);

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

        // Расписания исполняет служба (без вошедшего пользователя) — встроенного в GUI таймера больше нет.

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
        // Историю пишет служба; «Обзор» подтянет результат через неё. Шифрование: фразу отдаём службе
        // разовым тикетом (StashPassphrase) — открытый текст по пайпу один раз, дальше служба шифрует.
        string? jobId = null;
        var ok = false;
        string? error = null;
        var summary = "";
        try
        {
            var client = await ServiceConnection.GetClientAsync(ct)
                ?? throw new InvalidOperationException(ServiceConnection.Shared.Error ?? "Служба eBackup недоступна.");

            // Регистрируем выбранные «свои папки» — служба бэкапит только зарегистрированные (не сырой путь с провода).
            foreach (var folder in request.FolderPaths)
                await client.UpsertCustomFolderAsync(folder, ct);

            // Шифрование: фразу отдаём службе ОДИН раз перед стартом (тикет, TTL 30с) — она шифрует машинной стороной.
            PassphraseRef? passphraseRef = null;
            if (!string.IsNullOrEmpty(request.Passphrase))
                passphraseRef = PassphraseRef.FromTicket((await client.StashPassphraseAsync(request.Passphrase, ct)).Ticket);

            var settings = AppSettings.Load();
            // Параметры прогона: расписание несёт свои (ручной запуск = плановый); иначе — настройки приложения.
            var resp = await client.StartBackupAsync(new StartBackupRequest
            {
                ModuleIds = request.ModuleIds.ToArray(),
                CustomFolderIds = request.FolderPaths.ToArray(),
                TargetStorageIds = request.TargetStorageIds.ToArray(),
                Passphrase = passphraseRef,
                CompressionMode = request.CompressionMode ?? settings.CompressionMode,
                IncludeMachineName = request.IncludeMachineName ?? settings.IncludeMachineNameInArchive,
                RetentionCount = request.RetentionCount ?? (settings.RetentionCount > 0 ? settings.RetentionCount : null),
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

        try
        {
            // Архив в хранилище службы → восстановление ВЫПОЛНЯЕТ служба (SYSTEM = админ: Program Files
            // и профиль per-SID; лог в «Истории»). Отдельный локальный .ebk (открыт файлом) — в окне.
            if (request.Source.StorageId is not null)
                await RestoreViaServiceAsync(request);
            else
                await RestoreLocalFileAsync(request);
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Ошибка восстановления";
            StatusSub.Text = ex.Message;
        }
        finally
        {
            _operationRunning = false;
            BackupBtn.IsEnabled = true;
            BackupCompleted?.Invoke();
            await FadeOutFillAsync();
        }
    }

    /// <summary>Восстановление архива из хранилища службы — задачей в службе (живой прогресс из нот).</summary>
    private async Task RestoreViaServiceAsync(RestoreRequest request)
    {
        var client = await ServiceConnection.GetClientAsync()
            ?? throw new InvalidOperationException(ServiceConnection.Shared.Error ?? "Служба eBackup недоступна.");

        // Шифрование: фразу отдаём службе разовым тикетом — служба проверит её (проба по 1-му чанку) и расшифрует.
        // Неверная фраза вернётся как Failed «Неверная парольная фраза.»; повтор ввода минтит свежий тикет.
        PassphraseRef? passphraseRef = null;
        if (!string.IsNullOrEmpty(request.Passphrase))
            passphraseRef = PassphraseRef.FromTicket((await client.StashPassphraseAsync(request.Passphrase)).Ticket);

        var resp = await client.StartRestoreAsync(new StartRestoreRequest
        {
            SourceStorageId = request.Source.StorageId!,
            RemoteName = request.Source.RemoteName!,
            TargetDir = request.TargetDir,
            Policy = request.Policy.ToString(),
            AssetsDir = request.AssetsDir,
            Passphrase = passphraseRef,
            Trigger = "вручную",
            ClientRequestId = Guid.NewGuid().ToString("N"),
        });

        await foreach (var f in client.AttachToJobAsync(resp.JobId, 0))
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

        var job = await client.GetJobAsync(new GetJobRequest { JobId = resp.JobId });
        SetFill(1.0);
        if (job.State == "Completed")
        {
            StatusTitle.Text = "Готов к работе";
            StatusSub.Text = $"восстановлено {DateTime.Now:HH:mm}: {request.Source.RemoteName} → "
                + (request.TargetDir ?? "исходные места");
        }
        else
        {
            StatusTitle.Text = "Восстановление с ошибками";
            StatusSub.Text = "✕ " + (job.Error ?? "не удалось восстановить");
        }
    }

    /// <summary>Восстановление отдельного локального .ebk (открыт файлом) — в окне под пользователем.</summary>
    private async Task RestoreLocalFileAsync(RestoreRequest request)
    {
        var archive = request.Source.LocalPath!;
        if (Core.Crypto.ArchiveCipher.IsEncrypted(archive) && string.IsNullOrEmpty(request.Passphrase))
            throw new InvalidOperationException("Архив зашифрован — укажи парольную фразу.");

        var progress = new Progress<string>(s =>
        {
            StatusSub.Text = s;
            BumpFill(stageEnd: 0.95);
        });

        await Task.Run(() => new BackupEngine().RestoreAsync(
            archive,
            request.Modules,
            request.Policy,
            destinationRootOverride: request.TargetDir,
            assetsDirectory: request.AssetsDir,
            passphrase: request.Passphrase,
            progress: progress,
            log: null));

        SetFill(1.0);
        StatusTitle.Text = "Готов к работе";
        StatusSub.Text = $"восстановлено {DateTime.Now:HH:mm}: {Path.GetFileName(archive)} → "
            + (request.TargetDir ?? "исходные места");
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
