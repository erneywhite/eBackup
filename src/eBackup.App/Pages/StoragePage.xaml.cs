using System.ComponentModel;
using eBackup.Security;
using eBackup.Storage;
using eBackup.Storage.Sftp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

/// <summary>
/// Элемент списка хранилищ. INotifyPropertyChanged — чтобы значок селф-теста
/// (✓/✕/…) обновлялся в списке по мере прихода результатов.
/// </summary>
public sealed class StorageItem(SavedStorage storage) : INotifyPropertyChanged
{
    public SavedStorage Storage { get; } = storage;
    public string Title => Storage.Name;

    public string Subtitle => Storage.Kind switch
    {
        StorageKind.LocalFolder => Storage.Path ?? "",
        StorageKind.Sftp => $"sftp · {Storage.Username}@{Storage.Host}:{Storage.Port}",
        StorageKind.Ftp => $"{(Storage.UseFtps ? "ftps" : "ftp")} · {Storage.Username}@{Storage.Host}:{Storage.Port}",
        StorageKind.S3 => $"s3 · {Storage.Bucket}",
        StorageKind.WebDav => $"webdav · {Storage.ServiceUrl}",
        StorageKind.GoogleDrive => $"gdrive · папка {(string.IsNullOrWhiteSpace(Storage.RemoteDirectory) ? "eBackup" : Storage.RemoteDirectory)}",
        StorageKind.Dropbox => "dropbox · папка приложения" +
            (string.IsNullOrWhiteSpace(Storage.RemoteDirectory) ? "" : $"/{Storage.RemoteDirectory!.Trim('/')}"),
        _ => Storage.Kind.ToString()
    };

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

public sealed partial class StoragePage : Page
{
    private readonly StorageStore _store = new(new DpapiSecretProtector());
    private List<SavedStorage> _storages = [];
    private List<StorageItem> _items = [];
    private SavedStorage? _editing;            // null — создаём новое
    private StorageKind _editorKind;           // тип редактируемого/создаваемого
    private string? _pendingGDriveToken;       // refresh-токен после «Войти», ещё не сохранён
    private string? _pendingDropboxToken;

    // OAuth-вход держит loopback-порт до завершения (до 3 минут, если редирект не
    // пришёл). Статически — чтобы новая попытка отменяла прошлую даже с пересозданной
    // страницы, а не падала на занятом порту.
    private static CancellationTokenSource? _activeOAuthCts;
    private static Task? _activeOAuthTask;
    private CancellationTokenSource? _pageOAuthCts; // попытка, начатая ЭТОЙ страницей
    private SftpStorageProvider? _browseProvider;
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
            // Результат входа всё равно приземлился бы в мёртвую страницу — отменяем,
            // заодно освобождая loopback-порт для следующей попытки.
            _pageOAuthCts?.Cancel();
        };
    }

    // ---------- список ----------

    private async Task ReloadAsync(string? selectId)
    {
        List<SavedStorage> loaded;
        try
        {
            loaded = (await _store.LoadAsync()).ToList();
        }
        catch (Exception ex)
        {
            _storages = [];
            _items = [];
            StorageList.ItemsSource = null;
            Editor.Visibility = Visibility.Collapsed;
            EmptyHint.Text = "Не удалось прочитать конфиг хранилищ:\n" + ex.Message;
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        _storages = loaded
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _items = _storages.Select(s => new StorageItem(s)).ToList();

        _suppressSelection = true;
        StorageList.ItemsSource = _items;
        _suppressSelection = false;

        var toSelect = selectId is null ? null : _items.FirstOrDefault(i => i.Storage.Id == selectId);
        if (toSelect is not null)
        {
            StorageList.SelectedItem = toSelect;
        }
        else
        {
            Editor.Visibility = Visibility.Collapsed;
            EmptyHint.Text = "Выбери хранилище слева или добавь новое";
            EmptyHint.Visibility = Visibility.Visible;
        }
    }

    private void StorageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || StorageList.SelectedItem is not StorageItem item)
            return;

        _editing = item.Storage;
        _editorKind = item.Storage.Kind;
        ShowEditorFor(item.Storage);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e) => StartNew(StorageKind.LocalFolder);
    private void AddSftp_Click(object sender, RoutedEventArgs e) => StartNew(StorageKind.Sftp);
    private void AddFtp_Click(object sender, RoutedEventArgs e) => StartNew(StorageKind.Ftp);
    private void AddS3_Click(object sender, RoutedEventArgs e) => StartNew(StorageKind.S3);
    private void AddWebDav_Click(object sender, RoutedEventArgs e) => StartNew(StorageKind.WebDav);
    private void AddGDrive_Click(object sender, RoutedEventArgs e) => StartNew(StorageKind.GoogleDrive);
    private void AddDropbox_Click(object sender, RoutedEventArgs e) => StartNew(StorageKind.Dropbox);

    private void StartNew(StorageKind kind)
    {
        _suppressSelection = true;
        StorageList.SelectedItem = null;
        _suppressSelection = false;

        _editing = null;
        _editorKind = kind;
        ShowEditorFor(null);
    }

    // ---------- редактор ----------

    private void ShowEditorFor(SavedStorage? s)
    {
        EmptyHint.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;

        EditorTitle.Text = s?.Name ?? _editorKind switch
        {
            StorageKind.LocalFolder => "Новая папка / сетевой диск",
            StorageKind.Ftp => "Новое FTP-подключение",
            StorageKind.S3 => "Новое S3-хранилище",
            StorageKind.WebDav => "Новое WebDAV-хранилище",
            StorageKind.GoogleDrive => "Google Drive",
            StorageKind.Dropbox => "Dropbox",
            _ => "Новое SFTP-подключение"
        };
        NameBox.Text = s?.Name ?? string.Empty;
        _pendingGDriveToken = null;
        _pendingDropboxToken = null;

        FolderPanel.Visibility = _editorKind == StorageKind.LocalFolder ? Visibility.Visible : Visibility.Collapsed;
        SftpPanel.Visibility = _editorKind == StorageKind.Sftp ? Visibility.Visible : Visibility.Collapsed;
        FtpPanel.Visibility = _editorKind == StorageKind.Ftp ? Visibility.Visible : Visibility.Collapsed;
        S3Panel.Visibility = _editorKind == StorageKind.S3 ? Visibility.Visible : Visibility.Collapsed;
        WebDavPanel.Visibility = _editorKind == StorageKind.WebDav ? Visibility.Visible : Visibility.Collapsed;
        GDrivePanel.Visibility = _editorKind == StorageKind.GoogleDrive ? Visibility.Visible : Visibility.Collapsed;
        DropboxPanel.Visibility = _editorKind == StorageKind.Dropbox ? Visibility.Visible : Visibility.Collapsed;

        // OAuth-облака: статус подключения аккаунта
        GDriveFolderBox.Text = s?.Kind == StorageKind.GoogleDrive ? s.RemoteDirectory ?? "" : "";
        DropboxFolderBox.Text = s?.Kind == StorageKind.Dropbox ? s.RemoteDirectory ?? "" : "";
        SetOAuthStatus(GDriveStatus, s?.Kind == StorageKind.GoogleDrive && s.ProtectedOAuthToken is not null);
        SetOAuthStatus(DropboxStatus, s?.Kind == StorageKind.Dropbox && s.ProtectedOAuthToken is not null);

        // WebDAV
        WebDavUrlBox.Text = s?.Kind == StorageKind.WebDav ? s.ServiceUrl ?? "" : "";
        WebDavDirBox.Text = s?.Kind == StorageKind.WebDav ? s.RemoteDirectory ?? "" : "";
        WebDavUserBox.Text = s?.Kind == StorageKind.WebDav ? s.Username ?? "" : "";
        WebDavPassBox.Password = string.Empty;
        WebDavPassBox.PlaceholderText = s?.Kind == StorageKind.WebDav && s.ProtectedPassword is not null
            ? "пусто — оставить прежний"
            : "пароль";

        // S3
        S3UrlBox.Text = s?.Kind == StorageKind.S3 ? s.ServiceUrl ?? "" : "";
        S3BucketBox.Text = s?.Kind == StorageKind.S3 ? s.Bucket ?? "" : "";
        S3PrefixBox.Text = s?.Kind == StorageKind.S3 ? s.RemoteDirectory ?? "" : "";
        S3AccessBox.Text = s?.Kind == StorageKind.S3 ? s.AccessKeyId ?? "" : "";
        S3PathStyleCheck.IsChecked = s?.Kind != StorageKind.S3 || s.ForcePathStyle;
        S3SecretBox.Password = string.Empty;
        S3SecretBox.PlaceholderText = s?.Kind == StorageKind.S3 && s.ProtectedSecretKey is not null
            ? "пусто — оставить прежний"
            : "Secret Access Key";

        // FTP
        FtpHostBox.Text = s?.Kind == StorageKind.Ftp ? s.Host ?? "" : "";
        FtpPortBox.Text = (s?.Kind == StorageKind.Ftp ? s.Port : 21).ToString();
        FtpUserBox.Text = s?.Kind == StorageKind.Ftp ? s.Username ?? "" : "";
        FtpDirBox.Text = s?.Kind == StorageKind.Ftp ? s.RemoteDirectory ?? "." : ".";
        FtpsCheck.IsChecked = s?.Kind == StorageKind.Ftp && s.UseFtps;
        FtpAllowUntrustedCheck.IsChecked = s?.Kind == StorageKind.Ftp && s.AllowUntrustedCertificate;
        FtpPassBox.Password = string.Empty;
        FtpPassBox.PlaceholderText = s?.Kind == StorageKind.Ftp && s.ProtectedPassword is not null
            ? "пусто — оставить прежний"
            : "пароль";

        // Папка / сетевой диск
        PathBox.Text = s?.Path ?? string.Empty;
        ShareUserBox.Text = s?.ShareUsername ?? string.Empty;
        SharePassBox.Password = string.Empty;
        SharePassBox.PlaceholderText = s?.ProtectedSharePassword is null
            ? "пароль"
            : "пусто — оставить прежний";

        // SFTP
        HostBox.Text = s?.Host ?? string.Empty;
        PortBox.Text = (s?.Port ?? 22).ToString();
        UserBox.Text = s?.Username ?? string.Empty;
        RemoteDirBox.Text = s?.RemoteDirectory ?? ".";
        var keyAuth = s?.ProtectedPrivateKey is not null;
        AuthKey.IsChecked = keyAuth;
        AuthPassword.IsChecked = !keyAuth;
        PassBox.Password = string.Empty;
        KeyPemBox.Text = string.Empty;
        KeyPassBox.Password = string.Empty;
        PassBox.PlaceholderText = s?.ProtectedPassword is null ? "пароль" : "пусто — оставить прежний";
        KeyPemBox.PlaceholderText = s?.ProtectedPrivateKey is null
            ? "-----BEGIN OPENSSH PRIVATE KEY-----  (вставь содержимое приватного ключа)"
            : "пусто — оставить прежний ключ";
        KeyPassBox.PlaceholderText = s?.ProtectedKeyPassphrase is null
            ? "парольная фраза ключа (если есть)"
            : "пусто — оставить прежнюю";

        DeleteBtn.Visibility = s is null ? Visibility.Collapsed : Visibility.Visible;
        ResetTransientUi();
    }

    private void ResetTransientUi()
    {
        SetStatus(string.Empty, ok: true);
        TreePanel.Visibility = Visibility.Collapsed;
        FolderTree.RootNodes.Clear();
        _browseProvider = null;
    }

    private void SetOAuthStatus(TextBlock status, bool connected)
    {
        status.Text = connected ? "✓ аккаунт подключён" : "аккаунт не подключён";
        status.Foreground = connected
            ? (Brush)Application.Current.Resources["EbOkBrush"]
            : (Brush)Application.Current.Resources["EbTextDimBrush"];
    }

    /// <summary>
    /// Запускает OAuth-вход, предварительно отменив незавершённую прошлую попытку
    /// (она держит loopback-порт). null — вход отменили (повторный клик, закрытие
    /// редактора, уход со страницы), это не ошибка.
    /// </summary>
    private async Task<string?> RunOAuthLoginAsync(Func<CancellationToken, Task<string>> authorize)
    {
        _activeOAuthCts?.Cancel();
        if (_activeOAuthTask is not null)
            try { await _activeOAuthTask; } catch { /* прошлая попытка отменена или упала */ }

        var cts = new CancellationTokenSource();
        _activeOAuthCts = cts;
        _pageOAuthCts = cts;
        var task = authorize(cts.Token);
        _activeOAuthTask = task;
        try
        {
            return await task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async void GDriveLogin_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        // OAuth-ожидание отменяемо — «Закрыть» остаётся единственным способом его
        // прервать, не дожидаясь 3-минутного таймаута. Остальные busy-операции
        // отменить нельзя, поэтому там кнопка блокируется вместе со всеми.
        CloseEditorBtn.IsEnabled = true;
        SetStatus("Открываю браузер — подтверди вход в Google…", ok: true, dim: true);
        try
        {
            var token = await RunOAuthLoginAsync(GoogleDriveStorage.AuthorizeAsync);
            if (token is null)
            {
                SetStatus("Вход отменён.", ok: true, dim: true);
                return;
            }
            _pendingGDriveToken = token;
            SetOAuthStatus(GDriveStatus, connected: true);
            SetStatus("✓ Вход выполнен — нажми «Сохранить».", ok: true);
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

    private async void DropboxLogin_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        CloseEditorBtn.IsEnabled = true; // см. комментарий в GDriveLogin_Click
        SetStatus("Открываю браузер — подтверди вход в Dropbox…", ok: true, dim: true);
        try
        {
            var token = await RunOAuthLoginAsync(DropboxStorage.AuthorizeAsync);
            if (token is null)
            {
                SetStatus("Вход отменён.", ok: true, dim: true);
                return;
            }
            _pendingDropboxToken = token;
            SetOAuthStatus(DropboxStatus, connected: true);
            SetStatus("✓ Вход выполнен — нажми «Сохранить».", ok: true);
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

    private void AuthMode_Changed(object sender, RoutedEventArgs e)
    {
        if (PasswordPanel is null || KeyPanel is null)
            return; // событие во время InitializeComponent

        var keyAuth = AuthKey.IsChecked == true;
        PasswordPanel.Visibility = keyAuth ? Visibility.Collapsed : Visibility.Visible;
        KeyPanel.Visibility = keyAuth ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Открыть папку хранилища в проводнике (локальная или сетевая).</summary>
    private void OpenPathBtn_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (path.Length == 0)
        {
            SetStatus("Сначала укажи путь к папке.", ok: false);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus("✕ Не удалось открыть: " + ex.Message, ok: false);
        }
    }

    private async void BrowsePathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null)
            return;
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            PathBox.Text = folder.Path;
    }

    // ---------- сбор записи из формы ----------

    /// <summary>
    /// Собирает запись хранилища из формы. Пустые поля секретов при редактировании
    /// означают «оставить прежний» (Protected*-блоб переносится как есть).
    /// </summary>
    private SavedStorage? BuildFromForm(string id, string name, out string? error)
    {
        error = null;

        if (_editorKind == StorageKind.LocalFolder)
        {
            var path = PathBox.Text.Trim();
            if (path.Length == 0)
            {
                error = "Укажи путь к папке.";
                return null;
            }

            string? protectedPass = null;
            if (SharePassBox.Password.Length > 0)
                protectedPass = _store.Protect(SharePassBox.Password);
            else if (_editing?.ProtectedSharePassword is not null)
                protectedPass = _editing.ProtectedSharePassword;

            var shareUser = ShareUserBox.Text.Trim();
            return new SavedStorage
            {
                Id = id,
                Name = name,
                Kind = StorageKind.LocalFolder,
                Path = path,
                ShareUsername = shareUser.Length == 0 ? null : shareUser,
                ProtectedSharePassword = protectedPass
            };
        }

        if (_editorKind == StorageKind.GoogleDrive)
        {
            var token = _pendingGDriveToken is not null
                ? _store.Protect(_pendingGDriveToken)
                : _editing?.Kind == StorageKind.GoogleDrive ? _editing.ProtectedOAuthToken : null;
            if (token is null)
            {
                error = "Сначала войди в аккаунт Google.";
                return null;
            }

            return new SavedStorage
            {
                Id = id,
                Name = name,
                Kind = StorageKind.GoogleDrive,
                ProtectedOAuthToken = token,
                RemoteDirectory = GDriveFolderBox.Text.Trim()
            };
        }

        if (_editorKind == StorageKind.Dropbox)
        {
            var token = _pendingDropboxToken is not null
                ? _store.Protect(_pendingDropboxToken)
                : _editing?.Kind == StorageKind.Dropbox ? _editing.ProtectedOAuthToken : null;
            if (token is null)
            {
                error = "Сначала войди в аккаунт Dropbox.";
                return null;
            }

            return new SavedStorage
            {
                Id = id,
                Name = name,
                Kind = StorageKind.Dropbox,
                ProtectedOAuthToken = token,
                RemoteDirectory = DropboxFolderBox.Text.Trim()
            };
        }

        if (_editorKind == StorageKind.WebDav)
        {
            var davUrl = WebDavUrlBox.Text.Trim();
            var davUser = WebDavUserBox.Text.Trim();
            if (davUrl.Length == 0 || davUser.Length == 0)
            {
                error = "Укажи URL и логин.";
                return null;
            }

            string? davProtectedPass = null;
            if (WebDavPassBox.Password.Length > 0)
                davProtectedPass = _store.Protect(WebDavPassBox.Password);
            else if (_editing?.Kind == StorageKind.WebDav && _editing.ProtectedPassword is not null)
                davProtectedPass = _editing.ProtectedPassword;

            if (davProtectedPass is null)
            {
                error = "Введи пароль.";
                return null;
            }

            return new SavedStorage
            {
                Id = id,
                Name = name,
                Kind = StorageKind.WebDav,
                ServiceUrl = davUrl,
                Username = davUser,
                ProtectedPassword = davProtectedPass,
                RemoteDirectory = WebDavDirBox.Text.Trim()
            };
        }

        if (_editorKind == StorageKind.S3)
        {
            var url = S3UrlBox.Text.Trim();
            var bucket = S3BucketBox.Text.Trim();
            var access = S3AccessBox.Text.Trim();
            if (url.Length == 0 || bucket.Length == 0 || access.Length == 0)
            {
                error = "Укажи endpoint, бакет и Access Key ID.";
                return null;
            }

            string? protectedSecret = null;
            if (S3SecretBox.Password.Length > 0)
                protectedSecret = _store.Protect(S3SecretBox.Password);
            else if (_editing?.Kind == StorageKind.S3 && _editing.ProtectedSecretKey is not null)
                protectedSecret = _editing.ProtectedSecretKey;

            if (protectedSecret is null)
            {
                error = "Введи Secret Access Key.";
                return null;
            }

            return new SavedStorage
            {
                Id = id,
                Name = name,
                Kind = StorageKind.S3,
                ServiceUrl = url,
                Bucket = bucket,
                AccessKeyId = access,
                ProtectedSecretKey = protectedSecret,
                RemoteDirectory = S3PrefixBox.Text.Trim(),
                ForcePathStyle = S3PathStyleCheck.IsChecked == true
            };
        }

        if (_editorKind == StorageKind.Ftp)
        {
            var ftpHost = FtpHostBox.Text.Trim();
            var ftpUser = FtpUserBox.Text.Trim();
            if (ftpHost.Length == 0 || ftpUser.Length == 0)
            {
                error = "Укажи хост и логин.";
                return null;
            }

            int ftpPort;
            var ftpPortText = FtpPortBox.Text.Trim();
            if (ftpPortText.Length == 0)
            {
                ftpPort = 21;
            }
            else if (!int.TryParse(ftpPortText, out ftpPort) || ftpPort is <= 0 or > 65535)
            {
                error = "Порт должен быть числом от 1 до 65535.";
                return null;
            }

            string? ftpProtectedPass = null;
            if (FtpPassBox.Password.Length > 0)
                ftpProtectedPass = _store.Protect(FtpPassBox.Password);
            else if (_editing?.Kind == StorageKind.Ftp && _editing.ProtectedPassword is not null)
                ftpProtectedPass = _editing.ProtectedPassword;

            if (ftpProtectedPass is null)
            {
                error = "Введи пароль.";
                return null;
            }

            return new SavedStorage
            {
                Id = id,
                Name = name,
                Kind = StorageKind.Ftp,
                Host = ftpHost,
                Port = ftpPort,
                Username = ftpUser,
                ProtectedPassword = ftpProtectedPass,
                RemoteDirectory = FtpDirBox.Text.Trim() is { Length: > 0 } d ? d : ".",
                UseFtps = FtpsCheck.IsChecked == true,
                AllowUntrustedCertificate = FtpAllowUntrustedCheck.IsChecked == true
            };
        }

        // SFTP
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

        string? protectedPassword = null, protectedKey = null, protectedKeyPass = null;
        if (AuthKey.IsChecked == true)
        {
            if (KeyPemBox.Text.Trim().Length > 0)
                protectedKey = _store.Protect(KeyPemBox.Text);
            else if (_editing?.ProtectedPrivateKey is not null)
                protectedKey = _editing.ProtectedPrivateKey;

            if (protectedKey is null)
            {
                error = "Вставь содержимое приватного ключа.";
                return null;
            }

            if (KeyPassBox.Password.Length > 0)
                protectedKeyPass = _store.Protect(KeyPassBox.Password);
            else if (_editing?.ProtectedKeyPassphrase is not null)
                protectedKeyPass = _editing.ProtectedKeyPassphrase;
        }
        else
        {
            if (PassBox.Password.Length > 0)
                protectedPassword = _store.Protect(PassBox.Password);
            else if (_editing?.ProtectedPassword is not null)
                protectedPassword = _editing.ProtectedPassword;

            if (protectedPassword is null)
            {
                error = "Введи пароль.";
                return null;
            }
        }

        // Нормализация пути: SFTP не разворачивает «~», пути относительны домашней папки.
        var dir = RemoteDirBox.Text.Trim().Replace('\\', '/');
        if (dir == "~")
            dir = ".";
        else if (dir.StartsWith("~/", StringComparison.Ordinal))
            dir = dir[2..];

        return new SavedStorage
        {
            Id = id,
            Name = name,
            Kind = StorageKind.Sftp,
            Host = host,
            Port = port,
            Username = user,
            ProtectedPassword = protectedPassword,
            ProtectedPrivateKey = protectedKey,
            ProtectedKeyPassphrase = protectedKeyPass,
            RemoteDirectory = dir.Length == 0 ? "." : dir
        };
    }

    // ---------- действия ----------

    private async void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var built = BuildFromForm(_editing?.Id ?? "test", NameBox.Text.Trim(), out var error);
            if (built is null)
            {
                SetStatus(error!, ok: false);
                return;
            }

            SetStatus("Проверяю…", ok: true, dim: true);
            var result = await StorageFactory.Create(built, _store.Protector).TestAsync();
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
            var name = NameBox.Text.Trim();
            if (name.Length == 0)
                name = _editorKind switch
                {
                    StorageKind.LocalFolder => PathBox.Text.Trim(),
                    StorageKind.Ftp => FtpHostBox.Text.Trim(),
                    StorageKind.S3 => S3BucketBox.Text.Trim(),
                    StorageKind.WebDav => WebDavUrlBox.Text.Trim(),
                    StorageKind.GoogleDrive => "Google Drive",
                    StorageKind.Dropbox => "Dropbox",
                    _ => HostBox.Text.Trim()
                };

            // Сливаемся со СВЕЖИМ состоянием диска (параллельные правки не затираются).
            var fresh = (await _store.LoadAsync()).ToList();
            var id = _editing?.Id ?? MakeId(name, fresh);

            var built = BuildFromForm(id, name, out var error);
            if (built is null)
            {
                SetStatus(error!, ok: false);
                return;
            }

            await _store.SaveAllAsync(fresh.Where(s => s.Id != id).Append(built).ToList());
            await ReloadAsync(selectId: id);
            SetStatus("✓ Сохранено (секреты зашифрованы через DPAPI)", ok: true);

            var item = _items.FirstOrDefault(i => i.Storage.Id == id);
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

    /// <summary>Закрыть редактор без сохранения. Доступна и во время ожидания
    /// OAuth-входа (единственный способ прервать его, не дожидаясь таймаута).</summary>
    private void CloseEditorBtn_Click(object sender, RoutedEventArgs e)
    {
        _pageOAuthCts?.Cancel();

        _suppressSelection = true;
        StorageList.SelectedItem = null;
        _suppressSelection = false;

        _editing = null;
        _pendingGDriveToken = null;
        _pendingDropboxToken = null;
        ResetTransientUi();
        Editor.Visibility = Visibility.Collapsed;
        EmptyHint.Text = "Выбери хранилище слева или добавь новое";
        EmptyHint.Visibility = Visibility.Visible;
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null)
            return;

        var appRes = Application.Current.Resources;
        var dialog = new ContentDialog
        {
            Title = "Удалить хранилище?",
            Content = $"«{_editing.Name}» будет удалено из списка. Архивы в нём не трогаем.",
            PrimaryButtonText = "Удалить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            Background = (Brush)appRes["EbDialogBrush"],
            BorderBrush = (Brush)appRes["EbCardBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            CloseButtonStyle = (Style)appRes["EbDialogCloseStyle"]
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        SetBusy(true);
        try
        {
            var fresh = (await _store.LoadAsync()).Where(s => s.Id != _editing.Id).ToList();
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

    // ---------- селф-тест хранилищ ----------

    private void StartSelfTestTimer()
    {
        if (_selfTestTimer is not null)
            return;

        // Интервал из настроек; 0 — автопроверки нет (остаются проверка при
        // открытии страницы и кнопка ⟳). Перечитывается при следующем заходе.
        var minutes = AppSettings.Load().SelfTestMinutes;
        if (minutes <= 0)
            return;

        _selfTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
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

    /// <summary>Проверка одного хранилища; результат — значок в списке. Никогда не бросает.</summary>
    private async Task SelfTestOneAsync(StorageItem item)
    {
        item.StatusGlyph = "…";
        item.StatusBrush = (Brush)Application.Current.Resources["EbTextDimBrush"];
        try
        {
            var result = await StorageFactory.Create(item.Storage, _store.Protector).TestAsync();
            item.StatusGlyph = result.Success ? "✓" : "✕";
            item.StatusBrush = (Brush)Application.Current.Resources[result.Success ? "EbOkBrush" : "EbErrBrush"];
        }
        catch
        {
            item.StatusGlyph = "✕";
            item.StatusBrush = (Brush)Application.Current.Resources["EbErrBrush"];
        }
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
        => await SelfTestAllAsync();

    // ---------- дерево удалённых папок (SFTP) ----------

    private async void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var built = BuildFromForm(_editing?.Id ?? "browse", "browse", out var error);
            if (built is null || built.Kind != StorageKind.Sftp)
            {
                SetStatus(error ?? "Обзор доступен для SFTP.", ok: false);
                return;
            }

            SetStatus("Загружаю папки…", ok: true, dim: true);
            _browseProvider = new SftpStorageProvider(StorageFactory.ToSftpOptions(built, _store.Protector));
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

            // Пока грузили, могли выбрать другое хранилище — результат уже неактуален.
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
        CloseEditorBtn.IsEnabled = !busy;
        SaveBtn.IsEnabled = !busy;
        DeleteBtn.IsEnabled = !busy;
        BrowseBtn.IsEnabled = !busy;
        BrowsePathBtn.IsEnabled = !busy;
        GDriveLoginBtn.IsEnabled = !busy;
        DropboxLoginBtn.IsEnabled = !busy;
        // Список и «добавить» тоже блокируем: иначе результат незавершённой операции
        // (тест/обзор) приземлится в редактор уже другого хранилища.
        StorageList.IsEnabled = !busy;
        AddBtn.IsEnabled = !busy;
    }

    private void SetStatus(string text, bool ok, bool dim = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = dim
            ? (Brush)Application.Current.Resources["EbTextDimBrush"]
            : (Brush)Application.Current.Resources[ok ? "EbOkBrush" : "EbErrBrush"];
    }

    private static string MakeId(string name, IReadOnlyList<SavedStorage> existing)
    {
        var slug = new string(name.ToLowerInvariant()
                .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')
                .ToArray())
            .Trim('-');
        if (slug.Length == 0)
            slug = "storage";

        var id = slug;
        var n = 2;
        while (existing.Any(s => s.Id == id))
            id = $"{slug}-{n++}";
        return id;
    }
}
