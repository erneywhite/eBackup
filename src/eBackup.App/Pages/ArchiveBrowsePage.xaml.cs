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
    private string? _zipPath;        // готовый к чтению ZIP (после скачивания/расшифровки)
    private string? _encryptedPath;  // зашифрованный .ebk, ждёт парольную фразу
    private string? _tempDownloaded; // скачанная с сервера копия (удаляется при уходе)
    private string? _tempDecrypted;  // расшифрованная копия (удаляется при уходе)
    private bool _busy;

    public ArchiveBrowsePage()
    {
        InitializeComponent();
        Unloaded += (_, _) => Cleanup();
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
                SetStatus($"Скачиваю {_source.RemoteName} из «{saved.Name}»…", dim: true);
                var storage = StorageFactory.Create(saved, _store.Protector);
                _tempDownloaded = Path.Combine(
                    Path.GetTempPath(), "eBackup", "browse-" + _source.RemoteName);
                Directory.CreateDirectory(Path.GetDirectoryName(_tempDownloaded)!);
                await storage.DownloadAsync(_source.RemoteName!, _tempDownloaded);
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
            BuildTree();
        }
        catch (Exception ex)
        {
            SetStatus("✕ " + ex.Message, dim: false);
        }
    }

    private async void Decrypt_Click(object sender, RoutedEventArgs e)
    {
        if (_encryptedPath is null)
            return;
        if (PassBox.Password.Length == 0)
        {
            SetStatus("Введи парольную фразу.", dim: false);
            return;
        }

        try
        {
            SetStatus("Расшифровываю…", dim: true);
            var temp = Path.Combine(
                Path.GetTempPath(), "eBackup", $"browse-dec-{Guid.NewGuid():N}.ebk");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            await ArchiveCipher.DecryptAsync(_encryptedPath, temp, PassBox.Password);
            _tempDecrypted = temp;
            _zipPath = temp;
            PassPanel.Visibility = Visibility.Collapsed;
            BuildTree();
        }
        catch (Exception ex)
        {
            SetStatus("✕ Не удалось расшифровать: " + ex.Message, dim: false);
        }
    }

    /// <summary>Дерево содержимого: только data/… (манифест — служебный, не показываем).</summary>
    private void BuildTree()
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(_zipPath!);

        Tree.RootNodes.Clear();
        var folders = new Dictionary<string, TreeViewNode>(StringComparer.Ordinal);
        var files = 0;
        long totalBytes = 0;

        foreach (var entry in zip.Entries.OrderBy(en => en.FullName, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue; // маркер папки
            if (!entry.FullName.StartsWith("data/", StringComparison.Ordinal))
                continue; // manifest.json и прочее служебное

            var parts = entry.FullName["data/".Length..].Split('/');

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
                    (parent?.Children ?? Tree.RootNodes).Add(folder);
                }
                parent = folder;
            }

            (parent?.Children ?? Tree.RootNodes).Add(new TreeViewNode
            {
                Content = new ArchiveNode
                {
                    Name = parts[^1],
                    EntryFullName = entry.FullName,
                    Size = entry.Length
                }
            });
            files++;
            totalBytes += entry.Length;
        }

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
        if (_busy || _zipPath is null)
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
        try
        {
            SetStatus(destinationRoot is null
                ? $"Восстанавливаю {selected.Count} файлов по исходным путям…"
                : $"Скачиваю {selected.Count} файлов в {destinationRoot}…", dim: true);

            var zipPath = _zipPath;
            var engine = new BackupEngine();
            await Task.Run(() => engine.RestoreAsync(
                zipPath,
                conflictPolicy: ConflictPolicy.BackupExisting,
                destinationRootOverride: destinationRoot,
                entryFilter: selected.Contains));

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
        foreach (var path in new[] { _tempDownloaded, _tempDecrypted })
            if (path is not null)
                try { File.Delete(path); } catch { }
    }
}
