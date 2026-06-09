using System.ComponentModel;
using eBackup.Security;
using eBackup.Storage.Sftp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

/// <summary>
/// Элемент списка подключений. INotifyPropertyChanged — чтобы значок селф-теста
/// (✓/✕/…) обновлялся в списке по мере прихода результатов.
/// </summary>
public sealed class ConnItem(SavedSftpConnection connection) : INotifyPropertyChanged
{
    public SavedSftpConnection Connection { get; } = connection;
    public string Name => Connection.Name;
    public string Endpoint => $"{Connection.Username}@{Connection.Host}:{Connection.Port}";

    private string _statusGlyph = string.Empty;
    public string StatusGlyph
    {
        get => _statusGlyph;
        set { _statusGlyph = value; PropertyChanged?.Invoke(this, new(nameof(StatusGlyph))); }
    }

    private Brush? _statusBrush;
    public Brush? StatusBrush
    {
        get => _statusBrush;
        set { _statusBrush = value; PropertyChanged?.Invoke(this, new(nameof(StatusBrush))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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
    private const string DefaultEmptyHint = "Выбери подключение слева или добавь новое";

    private readonly SftpConnectionStore _store = new(new DpapiSecretProtector());
    private List<SavedSftpConnection> _connections = [];
    private List<ConnItem> _items = [];
    private SavedSftpConnection? _editing;          // null — создаём новое подключение
    private SftpStorageProvider? _browseProvider;   // провайдер текущей сессии обзора папок
    private bool _suppressSelection;
    private bool _selfTestRunning;
    private DispatcherTimer? _selfTestTimer;

    public StoragePage()
    {
        InitializeComponent();
        FolderTree.Expanding += FolderTree_Expanding;
        FolderTree.ItemInvoked += FolderTree_ItemInvoked;

        Loaded += async (_, _) =>
        {
            await ReloadAsync(selectId: null);
            StartSelfTestTimer();
            await SelfTestAllAsync();
        };
        Unloaded += (_, _) =>
        {
            _selfTestTimer?.Stop();
            _selfTestTimer = null;
        };
    }

    // ---------- список ----------

    /// <summary>Перечитывает конфиг с диска. Ошибки чтения показывает, а не роняет приложение.</summary>
    private async Task ReloadAsync(string? selectId)
    {
        List<SavedSftpConnection> loaded;
        try
        {
            loaded = (await _store.LoadAsync()).ToList();
        }
        catch (Exception ex)
        {
            _connections = [];
            _items = [];
            ConnList.ItemsSource = null;
            Editor.Visibility = Visibility.Collapsed;
            EmptyHint.Text = "Не удалось прочитать конфиг подключений:\n" + ex.Message;
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        _connections = loaded
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _items = _connections.Select(c => new ConnItem(c)).ToList();

        _suppressSelection = true;
        ConnList.ItemsSource = _items;
        _suppressSelection = false;

        var toSelect = selectId is null ? null : _items.FirstOrDefault(i => i.Connection.Id == selectId);
        if (toSelect is not null)
        {
            ConnList.SelectedItem = toSelect; // вызовет SelectionChanged → заполнит форму
        }
        else
        {
            Editor.Visibility = Visibility.Collapsed;
            EmptyHint.Text = DefaultEmptyHint;
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

        var keyAuth = item.Connection.ProtectedPrivateKey is not null;
        AuthKey.IsChecked = keyAuth;
        AuthPassword.IsChecked = !keyAuth;

        // Секреты не отображаем: пустое поле = «оставить как было».
        PassBox.Password = string.Empty;
        KeyPemBox.Text = string.Empty;
        KeyPassBox.Password = string.Empty;
        PassBox.PlaceholderText = item.Connection.ProtectedPassword is null
            ? "пароль"
            : "пусто — оставить прежний";
        KeyPemBox.PlaceholderText = item.Connection.ProtectedPrivateKey is null
            ? "-----BEGIN OPENSSH PRIVATE KEY-----  (вставь содержимое приватного ключа)"
            : "пусто — оставить прежний ключ";
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
        PassBox.Password = string.Empty;
        KeyPemBox.Text = string.Empty;
        KeyPassBox.Password = string.Empty;
        PassBox.PlaceholderText = "пароль";
        KeyPemBox.PlaceholderText = "-----BEGIN OPENSSH PRIVATE KEY-----  (вставь содержимое приватного ключа)";
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

    // ---------- селф-тест хранилищ ----------

    private void StartSelfTestTimer()
    {
        if (_selfTestTimer is not null)
            return;

        // Периодическая проверка доступности, пока экран открыт.
        _selfTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _selfTestTimer.Tick += async (_, _) => await SelfTestAllAsync();
        _selfTestTimer.Start();
    }

    private async Task SelfTestAllAsync()
    {
        if (_selfTestRunning)
            return;

        _selfTestRunning = true;
        try
        {
            var snapshot = _items.ToList();
            await Task.WhenAll(snapshot.Select(SelfTestOneAsync));
        }
        finally
        {
            _selfTestRunning = false;
        }
    }

    /// <summary>Проверка одного подключения; результат — значок в списке. Никогда не бросает.</summary>
    private async Task SelfTestOneAsync(ConnItem item)
    {
        item.StatusGlyph = "…";
        item.StatusBrush = (Brush)Application.Current.Resources["EbTextDimBrush"];
        try
        {
            var options = _store.Unprotect(item.Connection);
            var result = await new SftpStorageProvider(options).TestConnectionAsync();
            item.StatusGlyph = result.Success ? "✓" : "✕";
            item.StatusBrush = (Brush)Resources[result.Success ? "EbOkBrush" : "EbErrBrush"];
        }
        catch
        {
            // Например, DPAPI не расшифровал секреты (конфиг с другого ПК).
            item.StatusGlyph = "✕";
            item.StatusBrush = (Brush)Resources["EbErrBrush"];
        }
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
        => await SelfTestAllAsync();

    // ---------- сбор параметров из формы ----------

    /// <summary>
    /// Собирает параметры подключения из формы. Пустые поля секретов при редактировании
    /// означают «оставить прежний». Если сохранённые секреты не расшифровываются
    /// (конфиг с другого ПК) — не падаем, а просим ввести секрет заново.
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

        int port;
        var portText = PortBox.Text.Trim();
        if (portText.Length == 0)
        {
            port = 22;
        }
        else if (!int.TryParse(portText, out port) || port is <= 0 or > 65535)
        {
            error = "Порт должен быть числом от 1 до 65535.";
            return null;
        }

        // Сохранённые секреты расшифровываем аккуратно: DPAPI с другого ПК/пользователя
        // бросает — тогда считаем, что прежних секретов нет, и просим ввести заново.
        SftpConnectionOptions? kept = null;
        string? keptWarning = null;
        if (_editing is not null)
        {
            try
            {
                kept = _store.Unprotect(_editing);
            }
            catch
            {
                keptWarning = "Сохранённые секреты не удалось расшифровать (конфиг с другого ПК?) — введи их заново.";
            }
        }

        string? password = null, keyPem = null, keyPassphrase = null;
        if (AuthKey.IsChecked == true)
        {
            keyPem = KeyPemBox.Text.Trim().Length > 0 ? KeyPemBox.Text : kept?.PrivateKeyPem;
            if (string.IsNullOrWhiteSpace(keyPem))
            {
                error = keptWarning ?? "Вставь содержимое приватного ключа.";
                return null;
            }
            keyPassphrase = KeyPassBox.Password.Length > 0 ? KeyPassBox.Password : kept?.PrivateKeyPassphrase;
        }
        else
        {
            password = PassBox.Password.Length > 0 ? PassBox.Password : kept?.Password;
            if (string.IsNullOrEmpty(password))
            {
                error = keptWarning ?? "Введи пароль.";
                return null;
            }
        }

        // Нормализация пути: SFTP не разворачивает «~», а пути и так относительны
        // домашней папки; обратные слэши приводим к прямым.
        var dir = RemoteDirBox.Text.Trim().Replace('\\', '/');
        if (dir == "~")
            dir = ".";
        else if (dir.StartsWith("~/", StringComparison.Ordinal))
            dir = dir[2..];

        return new SftpConnectionOptions
        {
            Host = host,
            Port = port,
            Username = user,
            Password = password,
            PrivateKeyPem = keyPem,
            PrivateKeyPassphrase = keyPassphrase,
            RemoteDirectory = dir.Length == 0 ? "." : dir
        };
    }

    // ---------- действия ----------

    private async void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var options = BuildOptions(out var error);
            if (options is null)
            {
                SetStatus(error!, ok: false);
                return;
            }

            SetStatus("Проверяю подключение…", ok: true, dim: true);
            var result = await new SftpStorageProvider(options).TestConnectionAsync();
            SetStatus(result.Success ? "✓ " + result.Message : "✕ " + result.Message, result.Success);
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

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
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

            // Сливаемся со СВЕЖИМ состоянием диска, а не со снапшотом страницы:
            // подключения, добавленные через CLI параллельно, не должны затираться.
            var fresh = (await _store.LoadAsync()).ToList();
            var id = _editing?.Id ?? MakeId(name, fresh);
            var saved = _store.Protect(id, name, options);
            await _store.SaveAllAsync(fresh.Where(c => c.Id != id).Append(saved).ToList());

            await ReloadAsync(selectId: id);
            SetStatus("✓ Сохранено (секреты зашифрованы через DPAPI)", ok: true);

            var item = _items.FirstOrDefault(i => i.Connection.Id == id);
            if (item is not null)
                _ = SelfTestOneAsync(item);
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

        SetBusy(true);
        try
        {
            // Тот же принцип: удаляем из свежего состояния диска.
            var fresh = (await _store.LoadAsync()).Where(c => c.Id != _editing.Id).ToList();
            await _store.SaveAllAsync(fresh);
            _editing = null;
            await ReloadAsync(selectId: null);
        }
        catch (Exception ex)
        {
            SetStatus("✕ Не удалось удалить: " + ex.Message, ok: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- дерево удалённых папок ----------

    private async void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var options = BuildOptions(out var error);
            if (options is null)
            {
                SetStatus(error!, ok: false);
                return;
            }

            SetStatus("Загружаю папки…", ok: true, dim: true);
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
        var provider = _browseProvider;
        if (!args.Node.HasUnrealizedChildren || provider is null ||
            args.Node.Content is not DirNode dir)
            return;

        args.Node.HasUnrealizedChildren = false; // защита от повторной загрузки
        try
        {
            var dirs = await provider.ListDirectoriesAsync(dir.Path);

            // Пока грузили, могли выбрать другое подключение — результат уже неактуален.
            if (!ReferenceEquals(provider, _browseProvider))
                return;

            foreach (var d in dirs)
                args.Node.Children.Add(MakeNode(d));
        }
        catch (Exception ex)
        {
            args.Node.HasUnrealizedChildren = true; // дать возможность раскрыть повторно
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
        // Список и «добавить» тоже блокируем: иначе результат незавершённой операции
        // (тест/обзор) приземлится в редактор уже другого подключения.
        ConnList.IsEnabled = !busy;
        AddBtn.IsEnabled = !busy;
    }

    private void SetStatus(string text, bool ok, bool dim = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = dim
            ? (Brush)Application.Current.Resources["EbTextDimBrush"]
            : (Brush)Resources[ok ? "EbOkBrush" : "EbErrBrush"];
    }

    private static string MakeId(string name, IReadOnlyList<SavedSftpConnection> existing)
    {
        var slug = new string(name.ToLowerInvariant()
                .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')
                .ToArray())
            .Trim('-');
        if (slug.Length == 0)
            slug = "sftp";

        var id = slug;
        var n = 2;
        while (existing.Any(c => c.Id == id))
            id = $"{slug}-{n++}";
        return id;
    }
}
