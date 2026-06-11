using eBackup.Core.Crypto;
using eBackup.Core.Engine;
using eBackup.Security;
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
        >= 1L << 30 => $"{bytes / 1024.0 / 1024 / 1024:0.##} ГБ",
        >= 1L << 20 => $"{bytes / 1024.0 / 1024:0.#} МБ",
        _ => $"{Math.Max(1, bytes / 1024)} КБ"
    };
}

/// <summary>
/// Браузер архива: дерево содержимого с галочками; выбранное можно восстановить
/// по исходным путям или просто скачать в любую папку.
/// </summary>
public sealed partial class ArchiveBrowsePage : Page
{
    private readonly StorageStore _store = new(new DpapiSecretProtector());
    private RestoreSource? _source;
    private string? _zipPath;        // локальный ZIP (после скачивания/расшифровки)
    private Stream? _remoteStream;   // удалённый ZIP с произвольным доступом — без скачивания
    private string? _encryptedPath;  // зашифрованный .ebk, ждёт парольную фразу
    private string? _tempDownloaded; // скачанная с сервера копия (удаляется при уходе)
    private string? _tempDecrypted;  // расшифрованная копия (удаляется при уходе)
    private bool _busy;
    private bool _unloaded;
    private readonly CancellationTokenSource _cts = new();

    public ArchiveBrowsePage()
    {
        InitializeComponent();
        // Уход со страницы отменяет скачивание/расшифровку и подчищает временные
        // файлы. Обрыв не мгновенный, поэтому после каждого await в загрузочных
        // путях стоит проверка _unloaded с повторной чисткой.
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
                var storages = await _store.LoadAsync();
                var saved = storages.FirstOrDefault(s => s.Id == _source.StorageId)
                    ?? throw new InvalidOperationException("Хранилище-источник не найдено.");
                var storage = StorageFactory.Create(saved, _store.Protector);

                // Хранилище умеет произвольный доступ? Тогда читаем архив кусками —
                // без скачивания целиком (для 100+ ГБ это единственный разумный путь).
                // Исключение — зашифрованные: им нужен целый файл для расшифровки.
                if (storage is ISeekableArchiveStorage seekable)
                {
                    SetStatus("Читаю оглавление архива (без скачивания целиком)…", dim: true);
                    var stream = await seekable.OpenSeekableReadAsync(_source.RemoteName!, _cts.Token);
                    if (_unloaded)
                    {
                        stream.Dispose();
                        return;
                    }

                    var encrypted = await Task.Run(() => ArchiveCipher.IsEncrypted(stream));
                    if (!encrypted)
                    {
                        _remoteStream = stream;
                        TitleText.Text = _source.RemoteName;
                        await BuildTreeAsync();
                        return;
                    }

                    stream.Dispose(); // зашифрован — переходим к полному скачиванию
                }

                SetStatus($"Скачиваю {_source.RemoteName} из «{saved.Name}»…", dim: true);
                // GUID в имени: повторное открытие того же архива не должно
                // столкнуться с файлом, который ещё дописывает брошенная загрузка.
                _tempDownloaded = Path.Combine(Path.GetTempPath(), "eBackup",
                    $"browse-{Guid.NewGuid():N}-{_source.RemoteName}");
                Directory.CreateDirectory(Path.GetDirectoryName(_tempDownloaded)!);
                await storage.DownloadAsync(_source.RemoteName!, _tempDownloaded, _cts.Token);
                if (_unloaded)
                {
                    Cleanup();
                    return;
                }
                path = _tempDownloaded;
            }

            TitleText.Text = Path.GetFileName(path);

            if (ArchiveCipher.IsEncrypted(path))
            {
                _encryptedPath = path;
                PassPanel.Visibility = Visibility.Visible;
                SetStatus("Архив зашифрован — введи парольную фразу.", dim: true);
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
        if (_busy || _encryptedPath is null)
            return;
        if (PassBox.Password.Length == 0)
        {
            SetStatus("Введи парольную фразу.", dim: false);
            return;
        }

        _busy = true;
        try
        {
            SetStatus("Расшифровываю…", dim: true);
            var temp = Path.Combine(
                Path.GetTempPath(), "eBackup", $"browse-dec-{Guid.NewGuid():N}.ebk");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            // Путь запоминается ДО await: уйдём со страницы посреди расшифровки —
            // чистка всё равно будет знать, что удалять (открытый текст
            // зашифрованного архива не должен задерживаться в temp).
            _tempDecrypted = temp;
            await ArchiveCipher.DecryptAsync(_encryptedPath, temp, PassBox.Password, _cts.Token);
            if (_unloaded)
            {
                Cleanup();
                return;
            }
            _zipPath = temp;
            PassPanel.Visibility = Visibility.Collapsed;
            await BuildTreeAsync();
        }
        catch (OperationCanceledException)
        {
            Cleanup();
        }
        catch (Exception ex)
        {
            SetStatus("✕ Не удалось расшифровать: " + ex.Message, dim: false);
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
            ? "Архив пуст."
            : $"{files} файлов · {ArchiveNode.FormatSize(totalBytes)} — отметь нужное галочками "
              + "(галочка на папке выбирает всё внутри)", dim: true);
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
            SetStatus("Сначала отметь файлы галочками.", dim: false);
            return;
        }
        if (MainWindow.Instance?.IsBusy == true)
        {
            SetStatus("Идёт бэкап или восстановление — подожди завершения.", dim: false);
            return;
        }

        _busy = true;
        RestoreBtn.IsEnabled = DownloadBtn.IsEnabled = false;
        var window = MainWindow.Instance;
        var succeeded = false;
        try
        {
            SetStatus(destinationRoot is null
                ? $"Восстанавливаю {selected.Count} файлов по исходным путям…"
                : $"Скачиваю {selected.Count} файлов в {destinationRoot}…", dim: true);
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
                    entryFilter: selected.Contains)
                : engine.RestoreAsync(
                    zipPath!,
                    conflictPolicy: ConflictPolicy.BackupExisting,
                    destinationRootOverride: destinationRoot,
                    progress: progress,
                    entryFilter: selected.Contains));

            succeeded = true;
            SetStatus(destinationRoot is null
                ? $"✓ Восстановлено файлов: {selected.Count} (существовавшие сохранены как .bak)"
                : $"✓ Скачано файлов: {selected.Count} → {destinationRoot}", dim: false, ok: true);
        }
        catch (Exception ex)
        {
            SetStatus("✕ " + ex.Message, dim: false);
        }
        finally
        {
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

        foreach (var path in new[] { _tempDownloaded, _tempDecrypted })
            if (path is not null)
                try { File.Delete(path); } catch { }
    }
}
