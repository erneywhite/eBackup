using eBackup.App.Pages;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Core.Modules;
using eBackup.Core.Scheduling;
using eBackup.Modules.Obs;
using eBackup.Security;
using eBackup.Storage.Sftp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace eBackup.App;

/// <summary>Параметры запуска бэкапа, собранные страницей «Бэкап».</summary>
public sealed record BackupRequest(
    IReadOnlyList<IBackupModule> Modules,
    bool KeepLocal,
    IReadOnlyList<SavedSftpConnection> Targets,
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

    private readonly SftpConnectionStore _store = new(new DpapiSecretProtector());
    private readonly ModuleRegistry _modulesRegistry = new(
    [
        new BuiltInModuleSource([new ObsBackupModule()]),
        new DeclarativeModuleSource(),
    ]);
    private bool _operationRunning; // бэкап или восстановление — одновременно только одно
    private double _fill;           // текущая доля заливки-прогресса нижней панели (0..1)
    private DispatcherTimer? _scheduleTimer;
    private bool _checkingSchedules;

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

        ContentFrame.Navigate(typeof(OverviewPage));

        // Проверка расписаний: раз в минуту + сразу после запуска (догон пропущенных).
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _scheduleTimer.Tick += async (_, _) => await CheckSchedulesAsync();
        _scheduleTimer.Start();
        _ = CheckSchedulesAsync();
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Событие может прилететь во время InitializeComponent, когда Frame ещё не создан.
        if (ContentFrame is null || Nav.SelectedItem is not ListViewItem { Tag: string tag })
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

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        Nav.SelectedItem = null;
        if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
            ContentFrame.Navigate(typeof(SettingsPage));
    }

    // ---------- запуск бэкапа (вызывается страницей «Бэкап») ----------

    public async Task StartBackupAsync(BackupRequest request)
    {
        if (_operationRunning)
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
            // Если локально хранить не нужно — собираем во временной папке и удалим после заливки.
            var outDir = request.KeepLocal
                ? settings.LocalBackupDir
                : Path.Combine(Path.GetTempPath(), "eBackup");
            var name = BackupNaming.DefaultName(request.Modules);

            var engine = new BackupEngine();
            var archive = await Task.Run(() =>
                engine.CreateBackupAsync(request.Modules, outDir, name, request.Passphrase, progress));
            SetFill(request.Targets.Count == 0 ? 1.0 : 0.75); // архив готов

            var uploaded = new List<string>();
            var failed = new List<string>();
            for (var i = 0; i < request.Targets.Count; i++)
            {
                var conn = request.Targets[i];
                StatusSub.Text = $"Загружаю на {conn.Name}…";
                try
                {
                    var provider = new SftpStorageProvider(_store.Unprotect(conn));
                    await provider.UploadAsync(archive, Path.GetFileName(archive));
                    uploaded.Add(conn.Name);

                    // Хранение версий на сервере: оставляем последних N архивов.
                    if (settings.RetentionCount > 0)
                    {
                        StatusSub.Text = $"{conn.Name}: убираю старые архивы…";
                        var remote = await provider.ListDetailedAsync(); // уже отсортированы: свежие первыми
                        foreach (var old in remote.Skip(settings.RetentionCount))
                            await provider.DeleteAsync(old.Name);
                    }
                }
                catch (Exception ex)
                {
                    failed.Add($"{conn.Name}: {ex.Message}");
                }
                SetFill(0.75 + 0.25 * (i + 1) / request.Targets.Count); // заливка по целям
            }

            if (!request.KeepLocal)
            {
                try { File.Delete(archive); } catch { /* мусор в temp не критичен */ }
            }
            else if (settings.RetentionCount > 0)
            {
                // Хранение версий локально.
                ApplyLocalRetention(outDir, settings.RetentionCount);
            }

            var parts = new List<string>();
            if (request.KeepLocal) parts.Add(archive);
            if (uploaded.Count > 0) parts.Add("→ " + string.Join(", ", uploaded));

            StatusTitle.Text = failed.Count == 0 ? "Готов к работе" : "Бэкап завершён с ошибками";
            StatusSub.Text = $"последний бэкап: {DateTime.Now:HH:mm}  {string.Join("  ", parts)}"
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
            List<BackupSchedule> schedules;
            try
            {
                schedules = (await store.LoadAsync()).ToList();
            }
            catch
            {
                return; // битый файл расписаний не должен ронять приложение
            }

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

        List<SavedSftpConnection> targets;
        try
        {
            targets = (await _store.LoadAsync())
                .Where(c => s.TargetConnectionIds.Contains(c.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            targets = [];
        }

        if (!s.KeepLocal && targets.Count == 0)
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

        return new BackupRequest(modules, s.KeepLocal, targets, passphrase);
    }

    private static void ApplyLocalRetention(string dir, int keep)
    {
        try
        {
            var old = Directory.GetFiles(dir, "*.ebk")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Skip(keep);
            foreach (var f in old)
            {
                try { f.Delete(); } catch { /* занятый файл пропустим — удалится в следующий раз */ }
            }
        }
        catch
        {
            // чистка не должна ронять успешный бэкап
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

        string? tempDownloaded = null;
        try
        {
            // Источник: локальный файл либо скачивание с сервера во временную папку.
            string archive;
            if (request.Source.LocalPath is not null)
            {
                archive = request.Source.LocalPath;
            }
            else
            {
                var conns = await _store.LoadAsync();
                var conn = conns.FirstOrDefault(c => c.Id == request.Source.ConnectionId)
                    ?? throw new InvalidOperationException("Подключение-источник не найдено.");
                StatusSub.Text = $"Скачиваю {request.Source.RemoteName} с {conn.Name}…";
                var provider = new SftpStorageProvider(_store.Unprotect(conn));
                tempDownloaded = Path.Combine(Path.GetTempPath(), "eBackup", request.Source.RemoteName!);
                await provider.DownloadAsync(request.Source.RemoteName!, tempDownloaded);
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

    // ---------- заливка-прогресс нижней панели ----------

    /// <summary>Плавно довести заливку до доли <paramref name="fraction"/> (0..1).</summary>
    private void SetFill(double fraction)
    {
        _fill = Math.Clamp(fraction, 0, 1);
        ProgressFill.Opacity = 0.22;

        var anim = new DoubleAnimation
        {
            To = _fill,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, FillScale);
        Storyboard.SetTargetProperty(anim, "ScaleX");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    /// <summary>Небольшой сдвиг заливки вперёд внутри длинной фазы (асимптотически к её концу).</summary>
    private void BumpFill(double stageEnd)
        => SetFill(Math.Min(stageEnd, _fill + (stageEnd - _fill) * 0.18));

    private async Task FadeOutFillAsync()
    {
        await Task.Delay(1200);
        ProgressFill.Opacity = 0;
        FillScale.ScaleX = 0;
        _fill = 0;
    }
}
