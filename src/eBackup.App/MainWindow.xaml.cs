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

    // Вся вода — на правом крае-фронте. Мениск — пружинная модель ВДОЛЬ ВЫСОТЫ
    // панели: узел = горизонтальное отклонение фронта на своей высоте; узлы тянутся
    // к ровной кромке, обмениваются энергией с соседями, трение гасит. Плюс капли,
    // брызгающие вперёд при доливе (баллистика с гравитацией).
    private double[] _waveH = [];   // горизонтальное отклонение фронта, px
    private double[] _waveV = [];   // скорость узла
    private double _fillCur;        // текущая ширина заливки, px (догоняет цель плавно)
    private double _simTime;
    private bool _simRunning;
    private readonly List<Drop> _drops = [];
    private readonly Random _rng = new();
    private const double NodeStep = 6;       // шаг узлов мениска по вертикали, px
    private const double Spring = 0.03;      // жёсткость пружины к ровной кромке
    private const double Damping = 0.94;     // трение
    private const double Spread = 0.18;      // передача энергии соседям
    private const double MaxSwing = 16;      // предел колебания мениска, px

    private sealed class Drop
    {
        public double X, Y, Vx, Vy, Life;
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
        _drops.Clear();
        DropsLayer.Children.Clear();
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
        }

        // Заливка догоняет цель; при доливе мениск получает толчок и брызжет каплями.
        var target = _fill * width;
        var advance = (target - _fillCur) * 0.055;
        _fillCur += advance;
        if (advance > 0.05)
        {
            var node = _rng.Next(0, nodes);
            _waveV[node] += advance * 0.6;
            if (node > 0) _waveV[node - 1] += advance * 0.3;
            if (node + 1 < nodes) _waveV[node + 1] += advance * 0.3;

            if (advance > 0.25 && _rng.NextDouble() < 0.5)
                SpawnDrop(width, height, advance);
            if (advance > 1.2 && _rng.NextDouble() < 0.6)
                SpawnDrop(width, height, advance);
        }

        // Лёгкое фоновое колыхание мениска, чтобы он не застывал линейкой.
        _simTime += 1 / 60.0;
        for (var i = 0; i < nodes; i++)
            _waveV[i] += 0.016 * Math.Sin(_simTime * 2.1 + i * 0.9)
                       + 0.011 * Math.Sin(_simTime * 1.3 - i * 0.6);

        // Пружины + трение.
        for (var i = 0; i < nodes; i++)
        {
            _waveV[i] += -_waveH[i] * Spring;
            _waveV[i] *= Damping;
            _waveH[i] += _waveV[i];
        }

        // Волны разбегаются по мениску к соседям (два прохода для гладкости).
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

        for (var i = 0; i < nodes; i++)
            _waveH[i] = Math.Clamp(_waveH[i], -MaxSwing, MaxSwing);

        // Основной слой + «глубина» в противофазе чуть впереди.
        WaterFront.Data = BuildWater(0, 1.0, height);
        WaterBack.Data = BuildWater(4, -0.7, height);

        UpdateDrops(width, height);
    }

    /// <summary>Заливка во всю высоту от левого края до волнистого фронта-мениска.</summary>
    private Microsoft.UI.Xaml.Media.PathGeometry? BuildWater(double xOffset, double phase, double height)
    {
        if (_fillCur < 4)
            return null;

        var figure = new Microsoft.UI.Xaml.Media.PathFigure
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            IsClosed = true,
            IsFilled = true
        };

        var front = _fillCur + xOffset;
        for (var i = 0; i < _waveH.Length; i++)
            figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment
            {
                Point = new Windows.Foundation.Point(
                    Math.Max(0, front + _waveH[i] * phase),
                    Math.Min(i * NodeStep, height))
            });
        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment
        {
            Point = new Windows.Foundation.Point(
                Math.Max(0, front + _waveH[^1] * phase), height)
        });
        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment
        {
            Point = new Windows.Foundation.Point(0, height)
        });

        var geometry = new Microsoft.UI.Xaml.Media.PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>Капля срывается с мениска и летит вперёд по баллистике.</summary>
    private void SpawnDrop(double width, double height, double advance)
    {
        if (_drops.Count >= 14 || _fillCur > width - 14)
            return;

        var size = 3 + _rng.NextDouble() * 4;
        var visual = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xE3, 0xBD, 0xFB)),
            Opacity = 0.7
        };
        var drop = new Drop
        {
            X = _fillCur + 2,
            Y = _rng.NextDouble() * height * 0.6,
            Vx = 0.9 + Math.Min(advance, 4) * 0.7 + _rng.NextDouble() * 1.4,
            Vy = -0.8 + _rng.NextDouble() * 1.4,
            Life = 0.8 + _rng.NextDouble() * 0.5,
            Visual = visual
        };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(visual, drop.X);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(visual, drop.Y);
        DropsLayer.Children.Add(visual);
        _drops.Add(drop);
    }

    private void UpdateDrops(double width, double height)
    {
        for (var i = _drops.Count - 1; i >= 0; i--)
        {
            var d = _drops[i];
            d.X += d.Vx;
            d.Y += d.Vy;
            d.Vy += 0.16;   // гравитация
            d.Vx *= 0.992;  // сопротивление воздуха
            d.Life -= 1 / 60.0;

            // Капля умирает: впиталась во фронт, упала на дно, улетела или иссякла.
            if (d.Life <= 0 || d.X <= _fillCur - 4 || d.X >= width - 2 || d.Y >= height - 2)
            {
                DropsLayer.Children.Remove(d.Visual);
                _drops.RemoveAt(i);
                continue;
            }

            d.Visual.Opacity = Math.Min(0.7, d.Life * 1.6);
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(d.Visual, d.X);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(d.Visual, d.Y);
        }
    }
}
