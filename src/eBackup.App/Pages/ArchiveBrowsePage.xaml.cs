using eBackup.Core.Crypto;
using eBackup.Core.Engine;
using eBackup.Ipc.Client;
using eBackup.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace eBackup.App.Pages;

/// <summary>Узел дерева содержимого архива.</summary>
public sealed class ArchiveNode
{
    public required string Name { get; init; }

    /// <summary>null — папка; иначе полное имя записи ZIP («data/…»).</summary>
    public string? EntryFullName { get; init; }

    public long Size { get; init; }

    public override string ToString()
        => EntryFullName is null ? Name : $"{Name}  ·  {FormatSize(Size)}";

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => Loc.Get("ArchiveBrowse_SizeGb", bytes / 1024.0 / 1024 / 1024),
        >= 1L << 20 => Loc.Get("ArchiveBrowse_SizeMb", bytes / 1024.0 / 1024),
        _ => Loc.Get("ArchiveBrowse_SizeKb", Math.Max(1, bytes / 1024))
    };
}

/// <summary>
/// Браузер архива: дерево содержимого с галочками; выбранное можно восстановить
/// по исходным путям или просто скачать в любую папку.
/// </summary>
public sealed partial class ArchiveBrowsePage : Page
{
    private RestoreSource? _source;
    private string? _zipPath;        // локальный НЕзашифрованный ZIP
    private Stream? _remoteStream;   // seekable поток для листания/извлечения кусками: удалённый ZIP
                                     // ИЛИ расшифрованный на лету (SeekableEbkeStream поверх _encryptedSource)
    private Stream? _encryptedSource; // сырой seek-поток зашифрованного архива (удалённый RangeStream / локальный
                                      // файл): живёт между попытками ввода фразы, закрывается в Cleanup
    private bool _busy;
    private bool _unloaded;
    private readonly CancellationTokenSource _cts = new();

    public ArchiveBrowsePage()
    {
        InitializeComponent();
        // Уход со страницы отменяет чтение и закрывает потоки / seek-сессии службы.
        // Обрыв не мгновенный, поэтому после каждого await в загрузочных путях стоит
        // проверка _unloaded с повторной чисткой.
        Unloaded += (_, _) =>
        {
            _unloaded = true;
            try { _cts.Cancel(); } catch { }
            Cleanup();
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not RestoreSource source)
            return;
        _source = source;
        await PrepareAsync();
    }

    /// <summary>Довести архив до локального ZIP: скачать с сервера, спросить фразу.</summary>
    private async Task PrepareAsync()
    {
        try
        {
            string path;
            if (_source!.LocalPath is not null)
            {
                path = _source.LocalPath;
            }
            else
            {
                // Архив в хранилище службы: читаем его seek-сессией через службу — оглавление и
                // выбранные файлы тянутся кусками, без скачивания целиком (важно для 100+ ГБ).
                // Секрет хранилища у службы (машинный ключ), поэтому GUI открывает через неё.
                var client = await ServiceConnection.GetClientAsync(_cts.Token)
                    ?? throw new InvalidOperationException(ServiceConnection.Shared.Error ?? Loc.Get("ArchiveBrowse_ServiceUnavailable"));

                SetStatus(Loc.Get("ArchiveBrowse_ReadingToc"), dim: true);
                var open = await client.OpenArchiveReadAsync(_source.StorageId!, _source.RemoteName!, _cts.Token);
                var handle = open.Handle;
                var stream = new RangeStream(open.Length,
                    fetchRange: (off, cnt, c) => client.ReadArchiveChunkAsync(handle, off, cnt, c),
                    onDispose: () => { _ = client.CloseArchiveReadAsync(handle); }); // закрыть seek-сессию службы
                if (_unloaded)
                {
                    stream.Dispose();
                    return;
                }

                TitleText.Text = _source.RemoteName;

                var encrypted = await Task.Run(() => ArchiveCipher.IsEncrypted(stream), _cts.Token);
                if (_unloaded)
                {
                    stream.Dispose();
                    return;
                }
                if (encrypted)
                {
                    // Зашифрованный удалённый архив листаем и извлекаем по частям, расшифровывая нужные
                    // чанки на лету (SeekableEbkeStream) — нужна фраза. Поток держим до её ввода (и повторов).
                    _encryptedSource = stream;
                    PassPanel.Visibility = Visibility.Visible;
                    SetStatus(Loc.Get("ArchiveBrowse_EncryptedPrompt"), dim: true);
                    return;
                }

                _remoteStream = stream;
                await BuildTreeAsync();
                return;
            }

            TitleText.Text = Path.GetFileName(path);

            if (ArchiveCipher.IsEncrypted(path))
            {
                // Локальный файл открываем сразу seek-потоком — переиспользуем между попытками ввода фразы.
                _encryptedSource = File.OpenRead(path);
                PassPanel.Visibility = Visibility.Visible;
                SetStatus(Loc.Get("ArchiveBrowse_EncryptedPrompt"), dim: true);
                return;
            }

            _zipPath = path;
            await BuildTreeAsync();
        }
        catch (OperationCanceledException)
        {
            Cleanup(); // ушли со страницы во время скачивания
        }
        catch (Exception ex)
        {
            SetStatus("✕ " + ex.Message, dim: false);
            if (_unloaded)
                Cleanup();
        }
    }

    private async void Decrypt_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _encryptedSource is null)
            return;
        if (PassBox.Password.Length == 0)
        {
            SetStatus(Loc.Get("ArchiveBrowse_EnterPassphrase"), dim: false);
            return;
        }

        _busy = true;
        try
        {
            SetStatus(Loc.Get("ArchiveBrowse_CheckingPassphrase"), dim: true);

            // Источник (_encryptedSource) переиспользуем между попытками: при неверной фразе OpenAsync
            // (leaveOpen: true) его НЕ закрывает — можно сразу ввести правильную и повторить, не выходя
            // из «Открыть». Сам источник закроется в Cleanup. Дальше — обычный seek-путь (чтение кусками).
            var plain = await SeekableEbkeStream.OpenAsync(_encryptedSource, PassBox.Password, leaveOpen: true, _cts.Token);
            if (_unloaded)
            {
                plain.Dispose();
                Cleanup();
                return;
            }

            _remoteStream = plain;
            PassPanel.Visibility = Visibility.Collapsed;
            await BuildTreeAsync();
        }
        catch (OperationCanceledException)
        {
            Cleanup();
        }
        catch (InvalidDataException)
        {
            SetStatus("✕ " + Loc.Get("ArchiveBrowse_WrongPassphrase"), dim: false);
        }
        catch (Exception ex)
        {
            SetStatus("✕ " + Loc.Get("ArchiveBrowse_OpenFailed") + ex.Message, dim: false);
            if (_unloaded)
                Cleanup();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Дерево содержимого: только data/… (манифест — служебный, не показываем).
    /// Чтение ZIP — в фоне; узлы строятся в отсоединённый список и цепляются к
    /// живому дереву одним заходом — UI не виснет даже на десятках тысяч файлов.
    /// </summary>
    private async Task BuildTreeAsync()
    {
        var zipPath = _zipPath;
        var remote = _remoteStream;
        var entries = await Task.Run(() =>
        {
            // Удалённый поток: ZipArchive прочитает только центральный каталог
            // (несколько диапазонов с конца файла), не весь архив.
            using var zip = remote is not null
                ? new System.IO.Compression.ZipArchive(
                    remote, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true)
                : System.IO.Compression.ZipFile.OpenRead(zipPath!);
            return zip.Entries
                .Where(en => !string.IsNullOrEmpty(en.Name)
                    && en.FullName.StartsWith("data/", StringComparison.Ordinal))
                .OrderBy(en => en.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(en => (en.FullName, en.Length))
                .ToList();
        });
        if (_unloaded)
            return;

        var roots = new List<TreeViewNode>();
        var folders = new Dictionary<string, TreeViewNode>(StringComparer.Ordinal);
        var files = 0;
        long totalBytes = 0;

        foreach (var (fullName, length) in entries)
        {
            var parts = fullName["data/".Length..].Split('/');

            TreeViewNode? parent = null;
            var key = string.Empty;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                key += parts[i] + "/";
                if (!folders.TryGetValue(key, out var folder))
                {
                    folder = new TreeViewNode
                    {
                        Content = new ArchiveNode { Name = parts[i] },
                        IsExpanded = i == 0 // модули раскрыты, глубже — по клику
                    };
                    folders[key] = folder;
                    if (parent is null)
                        roots.Add(folder);
                    else
                        parent.Children.Add(folder);
                }
                parent = folder;
            }

            var fileNode = new TreeViewNode
            {
                Content = new ArchiveNode
                {
                    Name = parts[^1],
                    EntryFullName = fullName,
                    Size = length
                }
            };
            if (parent is null)
                roots.Add(fileNode);
            else
                parent.Children.Add(fileNode);

            files++;
            totalBytes += length;
            if (files % 2000 == 0)
                await Task.Yield(); // дышим, чтобы окно не помечалось «не отвечает»
        }
        if (_unloaded)
            return;

        Tree.RootNodes.Clear();
        foreach (var root in roots)
            Tree.RootNodes.Add(root);

        RestoreBtn.IsEnabled = DownloadBtn.IsEnabled = files > 0;
        SetStatus(files == 0
            ? Loc.Get("ArchiveBrowse_Empty")
            : Loc.Get("ArchiveBrowse_TreeSummary", files, ArchiveNode.FormatSize(totalBytes)), dim: true);
    }

    /// <summary>Полные имена выбранных записей ZIP (папки разворачиваются в файлы).</summary>
    private HashSet<string> SelectedEntries()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Tree.SelectedNodes)
            if (node.Content is ArchiveNode { EntryFullName: not null } n)
                set.Add(n.EntryFullName!);
        return set;
    }

    private async void RestoreSelected_Click(object sender, RoutedEventArgs e)
        => await RunSelectedAsync(destinationRoot: null);

    private async void DownloadSelected_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null)
            return;
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            await RunSelectedAsync(folder.Path);
    }

    /// <summary>
    /// Выборочное восстановление через движок: те же zip-slip-проверки и политика
    /// конфликтов, что и у полного. destinationRoot=null — по исходным путям.
    /// </summary>
    private async Task RunSelectedAsync(string? destinationRoot)
    {
        if (_busy || (_zipPath is null && _remoteStream is null))
            return;

        var selected = SelectedEntries();
        if (selected.Count == 0)
        {
            SetStatus(Loc.Get("ArchiveBrowse_SelectFirst"), dim: false);
            return;
        }
        if (MainWindow.Instance?.IsBusy == true)
        {
            SetStatus(Loc.Get("ArchiveBrowse_Busy"), dim: false);
            return;
        }

        _busy = true;
        RestoreBtn.IsEnabled = DownloadBtn.IsEnabled = false;
        var window = MainWindow.Instance;
        var succeeded = false;

        // ЛОКАЛЬНЫЙ лог выборочной операции (restore/извлечение идёт в окне, под пользователем).
        // NB: серверная «История» его пока НЕ показывает — выборочные операции живут локально (хвост 1.2,
        // примирить отдельной IPC-операцией записи истории; не блокер релиза).
        var history = new eBackup.Core.History.HistoryStore();
        var run = new eBackup.Core.History.BackupRunRecord
        {
            Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}",
            Operation = destinationRoot is null ? Loc.Get("ArchiveBrowse_OpRestoreSelective") : Loc.Get("ArchiveBrowse_OpExtract"),
            StartedAt = DateTimeOffset.Now,
            Trigger = Loc.Get("ArchiveBrowse_TriggerManual"),
            ArchiveName = TitleText.Text,
            Targets = [destinationRoot ?? Loc.Get("ArchiveBrowse_OriginalPaths")]
        };
        void Log(string message) => history.AppendLog(run.Id, message);
        Log($"Архив: {TitleText.Text}" + (_remoteStream is not null ? " (читается из хранилища кусками)" : ""));
        Log($"Выбрано файлов: {selected.Count} → {destinationRoot ?? "исходные пути"}");
        await history.SaveRunAsync(run);

        try
        {
            SetStatus(destinationRoot is null
                ? Loc.Get("ArchiveBrowse_RestoringN", selected.Count)
                : Loc.Get("ArchiveBrowse_DownloadingN", selected.Count, destinationRoot), dim: true);
            window?.ProgressStart(0.12);
            var progress = new Progress<string>(s =>
            {
                SetStatus(s, dim: true);
                window?.ProgressBump(0.9);
            });

            var zipPath = _zipPath;
            var remote = _remoteStream;
            var engine = new BackupEngine();
            await Task.Run(() => remote is not null
                ? engine.RestoreAsync(             // удалённо: тянутся только выбранные куски
                    remote,
                    conflictPolicy: ConflictPolicy.BackupExisting,
                    destinationRootOverride: destinationRoot,
                    progress: progress,
                    log: Log,
                    entryFilter: selected.Contains)
                : engine.RestoreAsync(
                    zipPath!,
                    conflictPolicy: ConflictPolicy.BackupExisting,
                    destinationRootOverride: destinationRoot,
                    progress: progress,
                    log: Log,
                    entryFilter: selected.Contains));

            succeeded = true;
            run.Success = true;
            SetStatus(destinationRoot is null
                ? Loc.Get("ArchiveBrowse_RestoredN", selected.Count)
                : Loc.Get("ArchiveBrowse_DownloadedN", selected.Count, destinationRoot), dim: false, ok: true);
        }
        catch (Exception ex)
        {
            run.Success = false;
            run.Error = ex.Message;
            Log("✕ Ошибка: " + ex.Message);
            SetStatus("✕ " + ex.Message, dim: false);
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.Now;
            Log(run.Success == true ? "Готово." : "Завершено с ошибкой.");
            await history.SaveRunAsync(run);

            _busy = false;
            RestoreBtn.IsEnabled = DownloadBtn.IsEnabled = true;
            if (window is not null)
                await window.ProgressFinishAsync(succeeded);
        }
    }

    private void SetStatus(string text, bool dim, bool ok = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)Application.Current.Resources[
            dim ? "EbTextDimBrush" : ok ? "EbOkBrush" : "EbErrBrush"];
    }

    private void Cleanup()
    {
        try { _remoteStream?.Dispose(); } catch { }
        _remoteStream = null;

        // Сырой источник зашифрованного архива закрываем отдельно: обёртка (SeekableEbkeStream,
        // leaveOpen) его не трогает. Для незашифрованных _encryptedSource null — двойного закрытия нет.
        try { _encryptedSource?.Dispose(); } catch { }
        _encryptedSource = null;
    }
}
