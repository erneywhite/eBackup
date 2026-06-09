using eBackup.Security;
using eBackup.Storage.Sftp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

/// <summary>Элемент списка подключений (обёртка для x:Bind).</summary>
public sealed class ConnItem(SavedSftpConnection connection)
{
    public SavedSftpConnection Connection { get; } = connection;
    public string Name => Connection.Name;
    public string Endpoint => $"{Connection.Username}@{Connection.Host}:{Connection.Port}";
}

/// <summary>Узел дерева удалённых папок (текст узла = последний сегмент пути).</summary>
public sealed class DirNode(string path)
{
    public string Path { get; } = path;

    public override string ToString()
    {
        var trimmed = Path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        var name = idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
        return name.Length > 0 ? name : Path;
    }
}

public sealed partial class StoragePage : Page
{
    private readonly SftpConnectionStore _store = new(new DpapiSecretProtector());
    private List<SavedSftpConnection> _connections = [];
    private SavedSftpConnection? _editing;          // null — создаём новое подключение
    private SftpStorageProvider? _browseProvider;   // провайдер для текущей сессии обзора папок
    private bool _suppressSelection;

    public StoragePage()
    {
        InitializeComponent();
        FolderTree.Expanding += FolderTree_Expanding;
        FolderTree.ItemInvoked += FolderTree_ItemInvoked;
        Loaded += async (_, _) => await ReloadAsync(selectId: null);
    }

    // ---------- список ----------

    private async Task ReloadAsync(string? selectId)
    {
        _connections = (await _store.LoadAsync())
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var items = _connections.Select(c => new ConnItem(c)).ToList();
        _suppressSelection = true;
        ConnList.ItemsSource = items;
        _suppressSelection = false;

        var toSelect = selectId is null ? null : items.FirstOrDefault(i => i.Connection.Id == selectId);
        if (toSelect is not null)
        {
            ConnList.SelectedItem = toSelect; // вызовет SelectionChanged → заполнит форму
        }
        else
        {
            Editor.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Visible;
        }
    }

    private void ConnList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || ConnList.SelectedItem is not ConnItem item)
            return;

        _editing = item.Connection;
        ShowEditor();

        EditorTitle.Text = item.Name;
        NameBox.Text = item.Connection.Name;
        HostBox.Text = item.Connection.Host;
        PortBox.Text = item.Connection.Port.ToString();
        UserBox.Text = item.Connection.Username;
        RemoteDirBox.Text = item.Connection.RemoteDirectory;

        var keyAuth = item.Connection.PrivateKeyPath is not null;
        AuthKey.IsChecked = keyAuth;
        AuthPassword.IsChecked = !keyAuth;
        KeyPathBox.Text = item.Connection.PrivateKeyPath ?? string.Empty;

        // Секреты не отображаем: пустое поле = «оставить как было».
        PassBox.Password = string.Empty;
        KeyPassBox.Password = string.Empty;
        PassBox.PlaceholderText = item.Connection.ProtectedPassword is null
            ? "пароль"
            : "пусто — оставить прежний";
        KeyPassBox.PlaceholderText = item.Connection.ProtectedKeyPassphrase is null
            ? "парольная фраза ключа (если есть)"
            : "пусто — оставить прежнюю";

        DeleteBtn.Visibility = Visibility.Visible;
        ResetTransientUi();
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        _suppressSelection = true;
        ConnList.SelectedItem = null;
        _suppressSelection = false;

        _editing = null;
        ShowEditor();

        EditorTitle.Text = "Новое подключение";
        NameBox.Text = string.Empty;
        HostBox.Text = string.Empty;
        PortBox.Text = "22";
        UserBox.Text = string.Empty;
        RemoteDirBox.Text = ".";
        AuthPassword.IsChecked = true;
        AuthKey.IsChecked = false;
        KeyPathBox.Text = string.Empty;
        PassBox.Password = string.Empty;
        KeyPassBox.Password = string.Empty;
        PassBox.PlaceholderText = "пароль";
        KeyPassBox.PlaceholderText = "парольная фраза ключа (если есть)";

        DeleteBtn.Visibility = Visibility.Collapsed;
        ResetTransientUi();
    }

    private void ShowEditor()
    {
        EmptyHint.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
    }

    private void ResetTransientUi()
    {
        SetStatus(string.Empty, ok: true);
        TreePanel.Visibility = Visibility.Collapsed;
        FolderTree.RootNodes.Clear();
        _browseProvider = null;
    }

    private void AuthMode_Changed(object sender, RoutedEventArgs e)
    {
        if (PasswordPanel is null || KeyPanel is null)
            return; // событие во время InitializeComponent

        var keyAuth = AuthKey.IsChecked == true;
        PasswordPanel.Visibility = keyAuth ? Visibility.Collapsed : Visibility.Visible;
        KeyPanel.Visibility = keyAuth ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- сбор параметров из формы ----------

    /// <summary>
    /// Собирает параметры подключения из формы. Пустые поля секретов при редактировании
    /// означают «оставить прежний» — тогда берём расшифрованное значение из сохранённого.
    /// </summary>
    private SftpConnectionOptions? BuildOptions(out string? error)
    {
        error = null;

        var host = HostBox.Text.Trim();
        var user = UserBox.Text.Trim();
        if (host.Length == 0 || user.Length == 0)
        {
            error = "Укажи хост и логин.";
            return null;
        }

        var port = int.TryParse(PortBox.Text.Trim(), out var p) && p is > 0 and < 65536 ? p : 22;
        var kept = _editing is null ? null : _store.Unprotect(_editing);

        string? password = null, keyPath = null, keyPassphrase = null;
        if (AuthKey.IsChecked == true)
        {
            keyPath = KeyPathBox.Text.Trim();
            if (keyPath.Length == 0)
            {
                error = "Укажи путь к приватному ключу.";
                return null;
            }
            keyPassphrase = PassOrKept(KeyPassBox.Password, kept?.PrivateKeyPassphrase);
        }
        else
        {
            password = PassOrKept(PassBox.Password, kept?.Password);
            if (string.IsNullOrEmpty(password))
            {
                error = "Введи пароль.";
                return null;
            }
        }

        var dir = RemoteDirBox.Text.Trim();
        return new SftpConnectionOptions
        {
            Host = host,
            Port = port,
            Username = user,
            Password = password,
            PrivateKeyPath = keyPath,
            PrivateKeyPassphrase = keyPassphrase,
            RemoteDirectory = dir.Length == 0 ? "." : dir
        };

        static string? PassOrKept(string entered, string? keptValue)
            => entered.Length > 0 ? entered : keptValue;
    }

    // ---------- действия ----------

    private async void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        var options = BuildOptions(out var error);
        if (options is null)
        {
            SetStatus(error!, ok: false);
            return;
        }

        SetBusy(true);
        SetStatus("Проверяю подключение…", ok: true, dim: true);
        try
        {
            var result = await new SftpStorageProvider(options).TestConnectionAsync();
            SetStatus(result.Success ? "✓ " + result.Message : "✕ " + result.Message, result.Success);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        var options = BuildOptions(out var error);
        if (options is null)
        {
            SetStatus(error!, ok: false);
            return;
        }

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
            name = options.Host;

        var id = _editing?.Id ?? MakeId(name);

        SetBusy(true);
        try
        {
            var saved = _store.Protect(id, name, options);
            var all = _connections.Where(c => c.Id != id).Append(saved).ToList();
            await _store.SaveAllAsync(all);
            await ReloadAsync(selectId: id);
            SetStatus("✓ Сохранено (секреты зашифрованы через DPAPI)", ok: true);
        }
        catch (Exception ex)
        {
            SetStatus("✕ Не удалось сохранить: " + ex.Message, ok: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null)
            return;

        var dialog = new ContentDialog
        {
            Title = "Удалить подключение?",
            Content = $"«{_editing.Name}» будет удалено. Архивы на сервере не трогаем.",
            PrimaryButtonText = "Удалить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var all = _connections.Where(c => c.Id != _editing.Id).ToList();
        await _store.SaveAllAsync(all);
        _editing = null;
        await ReloadAsync(selectId: null);
    }

    // ---------- дерево удалённых папок ----------

    private async void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var options = BuildOptions(out var error);
        if (options is null)
        {
            SetStatus(error!, ok: false);
            return;
        }

        SetBusy(true);
        SetStatus("Загружаю папки…", ok: true, dim: true);
        try
        {
            _browseProvider = new SftpStorageProvider(options);
            var dirs = await _browseProvider.ListDirectoriesAsync(".");

            FolderTree.RootNodes.Clear();
            foreach (var d in dirs)
                FolderTree.RootNodes.Add(MakeNode(d));

            TreePanel.Visibility = Visibility.Visible;
            SetStatus(dirs.Count == 0
                ? "В домашней папке нет подкаталогов — путь можно ввести вручную."
                : "Клик по папке подставит её в поле пути; раскрытие подгружает вложенные.", ok: true, dim: true);
        }
        catch (Exception ex)
        {
            SetStatus("✕ " + ex.Message, ok: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (!args.Node.HasUnrealizedChildren || _browseProvider is null ||
            args.Node.Content is not DirNode dir)
            return;

        args.Node.HasUnrealizedChildren = false; // защита от повторной загрузки
        try
        {
            var dirs = await _browseProvider.ListDirectoriesAsync(dir.Path);
            foreach (var d in dirs)
                args.Node.Children.Add(MakeNode(d));
        }
        catch (Exception ex)
        {
            SetStatus("✕ Не удалось открыть папку: " + ex.Message, ok: false);
        }
    }

    private void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: DirNode dir })
            RemoteDirBox.Text = dir.Path;
    }

    private static TreeViewNode MakeNode(string path)
        => new() { Content = new DirNode(path), HasUnrealizedChildren = true };

    // ---------- мелочи UI ----------

    private void SetBusy(bool busy)
    {
        TestBtn.IsEnabled = !busy;
        SaveBtn.IsEnabled = !busy;
        DeleteBtn.IsEnabled = !busy;
        BrowseBtn.IsEnabled = !busy;
    }

    private void SetStatus(string text, bool ok, bool dim = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = dim
            ? (Brush)Application.Current.Resources["EbTextDimBrush"]
            : (Brush)Resources[ok ? "EbOkBrush" : "EbErrBrush"];
    }

    private string MakeId(string name)
    {
        var slug = new string(name.ToLowerInvariant()
                .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')
                .ToArray())
            .Trim('-');
        if (slug.Length == 0)
            slug = "sftp";

        // На случай коллизии — добавим суффикс.
        var id = slug;
        var n = 2;
        while (_connections.Any(c => c.Id == id))
            id = $"{slug}-{n++}";
        return id;
    }
}
