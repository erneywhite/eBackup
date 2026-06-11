using eBackup.App.Pages;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Core.Modules;
using eBackup.Core.Scheduling;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace eBackup.App;

/// <summary>Параметры запуска бэкапа, собранные страницей «Бэкап» или расписанием.</summary>
public sealed record BackupRequest(
    IReadOnlyList<IBackupModule> Modules,
    IReadOnlyList<SavedStorage> Targets,
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
    private readonly ModuleRegistry _modulesRegistry = new(
    [
        new BuiltInModuleSource([new ObsBackupModule()]),
        new DeclarativeModuleSource(),
    ]);
    private bool _operationRunning; // бэкап или восстановление — одновременно только одно
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

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));

        // Минимальный размер окна: уже — и master-detail-страницы разваливаются.
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 940;
            presenter.PreferredMinimumHeight = 600;
        }

        InitLiquidProgress();

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

    private void GlobalBack_Click(object sender, RoutedEventArgs e) => TryGoBack();

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
        GlobalBackBtn.Visibility = ContentFrame.CanGoBack ? Visibility.Visible : Visibility.Collapsed;

        // Подсветка таба следует за фактической страницей (важно при переходах «назад»).
        var tag = e.SourcePageType == typeof(OverviewPage) ? "overview"
                : e.SourcePageType == typeof(ModulesPage) ? "modules"
                : e.SourcePageType == typeof(StoragePage) ? "storage"
                : e.SourcePageType == typeof(ArchivesPage) ? "archives"
                : e.SourcePageType == typeof(SchedulePage) ? "schedule"
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
            _ => typeof(OverviewPage)
        };

        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    private void BackupBtn_Click(object sender, RoutedEventArgs e)
    {
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

    public async Task StartBackupAsync(BackupRequest request)
    {
        if (_operationRunning || request.Targets.Count == 0)
            return;

        _operationRunning = true;
        BackupBtn.IsEnabled = false;
        StatusTitle.Text = "Делаю бэкап…";
        StatusSub.Text = "подготовка…";

        // Сборка архива занимает 0..70% заливки; каждое сообщение двигает её вперёд.
        SetFill(0.04);
        var progress = new Progress<string>(s =>
        {
            StatusSub.Text = s;
            BumpFill(stageEnd: 0.70);
        });

        var settings = AppSettings.Load();
        try
        {
            // Архив собирается во временной папке и раскладывается по всем целям одинаково.
            var buildDir = Path.Combine(Path.GetTempPath(), "eBackup");
            var name = BackupNaming.DefaultName(request.Modules);

            var engine = new BackupEngine();
            var archive = await Task.Run(() =>
                engine.CreateBackupAsync(request.Modules, buildDir, name, request.Passphrase, progress));
            SetFill(0.70);

            var done = new List<string>();
            var failed = new List<string>();
            for (var i = 0; i < request.Targets.Count; i++)
            {
                var target = request.Targets[i];
                StatusSub.Text = $"Сохраняю в «{target.Name}»…";
                try
                {
                    var storage = StorageFactory.Create(target, _storages.Protector);
                    await storage.UploadAsync(archive, Path.GetFileName(archive));
                    done.Add(target.Name);

                    // Хранение версий: одинаково для папок, SFTP и будущих облаков.
                    if (settings.RetentionCount > 0)
                    {
                        StatusSub.Text = $"{target.Name}: убираю старые архивы…";
                        var files = await storage.ListDetailedAsync();
                        foreach (var old in files.Skip(settings.RetentionCount))
                            await storage.DeleteAsync(old.Name);
                    }
                }
                catch (Exception ex)
                {
                    failed.Add($"{target.Name}: {ex.Message}");
                }
                SetFill(0.70 + 0.30 * (i + 1) / request.Targets.Count);
            }

            try { File.Delete(archive); } catch { /* temp */ }

            StatusTitle.Text = failed.Count == 0 ? "Готов к работе" : "Бэкап завершён с ошибками";
            StatusSub.Text = $"последний бэкап: {DateTime.Now:HH:mm}  → {string.Join(", ", done)}"
                + (failed.Count > 0 ? $"  ✕ {string.Join("; ", failed)}" : string.Empty);
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Ошибка бэкапа";
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
            await StartBackupAsync(request);
        }
        finally
        {
            _checkingSchedules = false;
        }
    }

    /// <summary>Собрать параметры бэкапа из расписания (свой набор настроек, глобальный тумблер не важен).</summary>
    private async Task<BackupRequest?> BuildRequestFromScheduleAsync(BackupSchedule s, ScheduleStore store)
    {
        var all = _modulesRegistry.LoadForRestore(); // все рабочие модули
        var modules = all
            .Where(m => s.ModuleIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Свои папки — СВОИ у каждого расписания (не зависят от страницы «Бэкап»).
        if (Core.Modules.CustomFolders.Build(s.CustomFolders) is { } foldersModule)
            modules.Add(foldersModule);

        if (modules.Count == 0)
            return null;

        List<SavedStorage> targets;
        try
        {
            var storages = (await _storages.LoadAsync()).ToList();
            targets = storages
                .Where(st => s.TargetConnectionIds.Contains(st.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();

            // Legacy: старые расписания с «локальной папкой» отдельным флагом.
            if (s.KeepLocal)
            {
                var local = storages.FirstOrDefault(st => st.Id == "local")
                    ?? storages.FirstOrDefault(st => st.Kind == StorageKind.LocalFolder);
                if (local is not null && targets.All(t => t.Id != local.Id))
                    targets.Add(local);
            }
        }
        catch
        {
            targets = [];
        }

        if (targets.Count == 0)
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

        return new BackupRequest(modules, targets, passphrase);
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

        string? tempDownloaded = null;
        try
        {
            // Источник: прямой путь (хранилище-папка) либо скачивание из хранилища в temp.
            string archive;
            if (request.Source.LocalPath is not null)
            {
                archive = request.Source.LocalPath;
            }
            else
            {
                var storages = await _storages.LoadAsync();
                var saved = storages.FirstOrDefault(s => s.Id == request.Source.StorageId)
                    ?? throw new InvalidOperationException("Хранилище-источник не найдено.");
                StatusSub.Text = $"Скачиваю {request.Source.RemoteName} из «{saved.Name}»…";
                var storage = StorageFactory.Create(saved, _storages.Protector);
                tempDownloaded = Path.Combine(Path.GetTempPath(), "eBackup", request.Source.RemoteName!);
                await storage.DownloadAsync(request.Source.RemoteName!, tempDownloaded);
                archive = tempDownloaded;
                SetFill(0.25);
            }

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
                progress: progress));

            SetFill(1.0);
            StatusTitle.Text = "Готов к работе";
            StatusSub.Text = $"восстановлено {DateTime.Now:HH:mm}: {Path.GetFileName(archive)} → "
                + (request.TargetDir is null ? "исходные места" : request.TargetDir);
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Ошибка восстановления";
            StatusSub.Text = ex.Message;
        }
        finally
        {
            if (tempDownloaded is not null)
            {
                try { File.Delete(tempDownloaded); } catch { /* temp */ }
            }
            _operationRunning = false;
            BackupBtn.IsEnabled = true;
            await FadeOutFillAsync();
        }
    }

    // ---------- «жидкая» заливка-прогресс нижней панели (вода с физикой) ----------

    // Поверхность воды — пружинная модель: каждый узел тянется к уровню покоя,
    // соседи обмениваются энергией (волны разбегаются), всё гасится трением.
    private double[] _waveH = [];   // отклонение поверхности от уровня, px
    private double[] _waveV = [];   // скорость узла
    private double _fillCur;        // текущая ширина воды, px (догоняет цель плавно)
    private double _simTime;
    private bool _simRunning;
    private const double NodeStep = 12;     // шаг узлов поверхности, px
    private const double Spring = 0.022;    // жёсткость пружины к уровню покоя
    private const double Damping = 0.965;   // трение
    private const double Spread = 0.12;     // передача энергии соседям

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

        var nodes = (int)(width / NodeStep) + 3;
        if (_waveH.Length != nodes)
        {
            _waveH = new double[nodes];
            _waveV = new double[nodes];
        }

        // Вода догоняет целевой уровень; от точки долива расходится всплеск.
        var target = _fill * width;
        var advance = (target - _fillCur) * 0.055;
        _fillCur += advance;
        if (advance > 0.05)
        {
            var front = Math.Clamp((int)(_fillCur / NodeStep), 1, nodes - 2);
            _waveV[front] += advance * 0.45;
            _waveV[front - 1] += advance * 0.25;
            if (front + 1 < nodes)
                _waveV[front + 1] += advance * 0.25;
        }

        // Лёгкое фоновое волнение, чтобы вода никогда не застывала зеркалом.
        _simTime += 1 / 60.0;
        for (var i = 0; i < nodes; i++)
            _waveV[i] += 0.010 * Math.Sin(_simTime * 1.9 + i * 0.55)
                       + 0.007 * Math.Sin(_simTime * 1.1 - i * 0.33);

        // Пружины + трение.
        for (var i = 0; i < nodes; i++)
        {
            _waveV[i] += -_waveH[i] * Spring;
            _waveV[i] *= Damping;
            _waveH[i] += _waveV[i];
        }

        // Волны разбегаются к соседям (два прохода для гладкости).
        for (var pass = 0; pass < 2; pass++)
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

        var maxSwing = height * 0.22;
        for (var i = 0; i < nodes; i++)
            _waveH[i] = Math.Clamp(_waveH[i], -maxSwing, maxSwing);

        // Передний слой — основная вода, задний — глубина (ниже и в противофазе).
        var level = height * 0.34;
        WaterFront.Data = BuildWater(level, 1.0, height);
        WaterBack.Data = BuildWater(level + 6, -0.7, height);
    }

    /// <summary>Геометрия воды: поверхность по узлам симуляции от 0 до текущего фронта.</summary>
    private Microsoft.UI.Xaml.Media.PathGeometry? BuildWater(double level, double phase, double height)
    {
        if (_fillCur < 6)
            return null;

        var figure = new Microsoft.UI.Xaml.Media.PathFigure
        {
            StartPoint = new Windows.Foundation.Point(0, level + _waveH[0] * phase),
            IsClosed = true,
            IsFilled = true
        };

        var lastNode = Math.Min((int)(_fillCur / NodeStep) + 1, _waveH.Length - 1);
        for (var i = 1; i <= lastNode; i++)
            figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment
            {
                Point = new Windows.Foundation.Point(
                    Math.Min(i * NodeStep, _fillCur), level + _waveH[i] * phase)
            });

        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment
        {
            Point = new Windows.Foundation.Point(_fillCur, height)
        });
        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment
        {
            Point = new Windows.Foundation.Point(0, height)
        });

        var geometry = new Microsoft.UI.Xaml.Media.PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
