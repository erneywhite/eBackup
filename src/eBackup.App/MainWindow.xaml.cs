using eBackup.App.Pages;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
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
    private bool _operationRunning; // бэкап или восстановление — одновременно только одно
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

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));

        ContentFrame.Navigate(typeof(OverviewPage));
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

    // ---------- запуск бэкапа (вызывается страницей «Бэкап») ----------

    private static string DefaultBackupDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "eBackup", "Backups");

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

        try
        {
            // Если локально хранить не нужно — собираем во временной папке и удалим после заливки.
            var outDir = request.KeepLocal
                ? DefaultBackupDir()
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
