using System.ComponentModel;
using eBackup.Ipc.Client;
using eBackup.Ipc.Contracts;
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
        StorageKind.GoogleDrive => Loc.Get("Storage_SubtitleGDrive", string.IsNullOrWhiteSpace(Storage.RemoteDirectory) ? "eBackup" : Storage.RemoteDirectory),
        StorageKind.Dropbox => Loc.Get("Storage_SubtitleDropbox") +
            (string.IsNullOrWhiteSpace(Storage.RemoteDirectory) ? "" : $"/{Storage.RemoteDirectory!.Trim('/')}"),
        StorageKind.Mega => Loc.Get("Storage_SubtitleMega", Storage.Username ?? "", string.IsNullOrWhiteSpace(Storage.RemoteDirectory) ? "eBackup" : Storage.RemoteDirectory),
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
    // Хранилища живут в СЛУЖБЕ (машинный ключ, ProgramData) — страница ими управляет по IPC.
    private List<SavedStorage> _storages = [];          // несекретная проекция для списка/формы
    private List<StorageDetail> _details = [];          // что вернула служба (поля + какие секреты есть)
    private List<StorageItem> _items = [];
    private SavedStorage? _editing;            // null — создаём новое
    private StorageDetail? _editingDetail;     // детали выбранного (для плейсхолдеров «оставить прежний»)
    private StorageKind _editorKind;           // тип редактируемого/создаваемого
    private string? _pendingGDriveToken;       // refresh-токен после «Войти», ещё не сохранён
    private string? _pendingDropboxToken;
    private string? _pendingMegaToken;         // сериализованная сессия MEGA после «Войти», ещё не сохранена

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

    // ---------- связь со службой ----------

    /// <summary>Живой клиент службы или null (с заполненным <see cref="ServiceConnection.Error"/>).</summary>
    private static async Task<IpcClient?> ConnectAsync()
        => await ServiceConnection.Shared.EnsureConnectedAsync().ConfigureAwait(true)
            ? ServiceConnection.Shared.Client
            : null;

    private static string ServiceError => ServiceConnection.Shared.Error ?? Loc.Get("Storage_ServiceUnavailable");

    /// <summary>Есть ли у выбранного хранилища секрет с таким именем (по данным службы).</summary>
    private bool HasSecret(string key) => _editingDetail?.PresentSecrets.Contains(key) == true;

    // ---------- список ----------

    private async Task ReloadAsync(string? selectId)
    {
        var client = await ConnectAsync();
        if (client is null)
        {
            _storages = []; _details = []; _items = [];
            StorageList.ItemsSource = null;
            Editor.Visibility = Visibility.Collapsed;
            EmptyHint.Text = Loc.Get("Storage_ServiceUnavailableTitle") + "\n" + ServiceError;
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            _details = (await client.ListStorageDetailsAsync()).ToList();
        }
        catch (Exception ex)
        {
            _storages = []; _details = []; _items = [];
            StorageList.ItemsSource = null;
            Editor.Visibility = Visibility.Collapsed;
            EmptyHint.Text = Loc.Get("Storage_ReadFailed") + "\n" + ex.Message;
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        _storages = _details
            .Select(ServiceStorage.ToSaved)
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
            EmptyHint.Text = Loc.Get("Storage_EmptyHintText");
            EmptyHint.Visibility = Visibility.Visible;
        }
    }

    private void StorageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || StorageList.SelectedItem is not StorageItem item)
            return;

        _editing = item.Storage;
        _editingDetail = _details.FirstOrDefault(d => d.Id == item.Storage.Id);
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
    private void AddMega_Click(object sender, RoutedEventArgs e) => StartNew(StorageKind.Mega);

    private void StartNew(StorageKind kind)
    {
        _suppressSelection = true;
        StorageList.SelectedItem = null;
        _suppressSelection = false;

        _editing = null;
        _editingDetail = null;
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
            StorageKind.LocalFolder => Loc.Get("Storage_NewFolderTitle"),
            StorageKind.Ftp => Loc.Get("Storage_NewFtpTitle"),
            StorageKind.S3 => Loc.Get("Storage_NewS3Title"),
            StorageKind.WebDav => Loc.Get("Storage_NewWebDavTitle"),
            StorageKind.GoogleDrive => "Google Drive",
            StorageKind.Dropbox => "Dropbox",
            StorageKind.Mega => "MEGA",
            _ => Loc.Get("Storage_NewSftpTitle")
        };
        NameBox.Text = s?.Name ?? string.Empty;
        _pendingGDriveToken = null;
        _pendingDropboxToken = null;
        _pendingMegaToken = null;

        FolderPanel.Visibility = _editorKind == StorageKind.LocalFolder ? Visibility.Visible : Visibility.Collapsed;
        SftpPanel.Visibility = _editorKind == StorageKind.Sftp ? Visibility.Visible : Visibility.Collapsed;
        FtpPanel.Visibility = _editorKind == StorageKind.Ftp ? Visibility.Visible : Visibility.Collapsed;
        S3Panel.Visibility = _editorKind == StorageKind.S3 ? Visibility.Visible : Visibility.Collapsed;
        WebDavPanel.Visibility = _editorKind == StorageKind.WebDav ? Visibility.Visible : Visibility.Collapsed;
        GDrivePanel.Visibility = _editorKind == StorageKind.GoogleDrive ? Visibility.Visible : Visibility.Collapsed;
        DropboxPanel.Visibility = _editorKind == StorageKind.Dropbox ? Visibility.Visible : Visibility.Collapsed;
        MegaPanel.Visibility = _editorKind == StorageKind.Mega ? Visibility.Visible : Visibility.Collapsed;

        // MEGA: e-mail/папка — обычные поля; пароль+2FA только для входа (не хранятся); статус — по наличию сессии.
        MegaEmailBox.Text = s?.Kind == StorageKind.Mega ? s.Username ?? "" : "";
        MegaDirBox.Text = s?.Kind == StorageKind.Mega ? s.RemoteDirectory ?? "" : "";
        MegaPassBox.Password = string.Empty;
        MegaMfaBox.Text = string.Empty;
        SetOAuthStatus(MegaStatus, s?.Kind == StorageKind.Mega && HasSecret("oauthToken"));

        // OAuth-облака: статус подключения аккаунта
        GDriveFolderBox.Text = s?.Kind == StorageKind.GoogleDrive ? s.RemoteDirectory ?? "" : "";
        DropboxFolderBox.Text = s?.Kind == StorageKind.Dropbox ? s.RemoteDirectory ?? "" : "";
        SetOAuthStatus(GDriveStatus, s?.Kind == StorageKind.GoogleDrive && HasSecret("oauthToken"));
        SetOAuthStatus(DropboxStatus, s?.Kind == StorageKind.Dropbox && HasSecret("oauthToken"));

        // WebDAV
        WebDavUrlBox.Text = s?.Kind == StorageKind.WebDav ? s.ServiceUrl ?? "" : "";
        WebDavDirBox.Text = s?.Kind == StorageKind.WebDav ? s.RemoteDirectory ?? "" : "";
        WebDavUserBox.Text = s?.Kind == StorageKind.WebDav ? s.Username ?? "" : "";
        WebDavPassBox.Password = string.Empty;
        WebDavPassBox.PlaceholderText = s?.Kind == StorageKind.WebDav && HasSecret("password")
            ? Loc.Get("Storage_KeepExisting")
            : Loc.Get("Storage_PasswordPlaceholder");

        // S3
        S3UrlBox.Text = s?.Kind == StorageKind.S3 ? s.ServiceUrl ?? "" : "";
        S3BucketBox.Text = s?.Kind == StorageKind.S3 ? s.Bucket ?? "" : "";
        S3PrefixBox.Text = s?.Kind == StorageKind.S3 ? s.RemoteDirectory ?? "" : "";
        S3AccessBox.Text = s?.Kind == StorageKind.S3 ? s.AccessKeyId ?? "" : "";
        S3PathStyleCheck.IsChecked = s?.Kind != StorageKind.S3 || s.ForcePathStyle;
        S3SecretBox.Password = string.Empty;
        S3SecretBox.PlaceholderText = s?.Kind == StorageKind.S3 && HasSecret("secretKey")
            ? Loc.Get("Storage_KeepExisting")
            : "Secret Access Key";

        // FTP
        FtpHostBox.Text = s?.Kind == StorageKind.Ftp ? s.Host ?? "" : "";
        FtpPortBox.Text = (s?.Kind == StorageKind.Ftp ? s.Port : 21).ToString();
        FtpUserBox.Text = s?.Kind == StorageKind.Ftp ? s.Username ?? "" : "";
        FtpDirBox.Text = s?.Kind == StorageKind.Ftp ? s.RemoteDirectory ?? "." : ".";
        FtpsCheck.IsChecked = s?.Kind == StorageKind.Ftp && s.UseFtps;
        FtpAllowUntrustedCheck.IsChecked = s?.Kind == StorageKind.Ftp && s.AllowUntrustedCertificate;
        FtpPassBox.Password = string.Empty;
        FtpPassBox.PlaceholderText = s?.Kind == StorageKind.Ftp && HasSecret("password")
            ? Loc.Get("Storage_KeepExisting")
            : Loc.Get("Storage_PasswordPlaceholder");

        // Папка / сетевой диск
        PathBox.Text = s?.Path ?? string.Empty;
        ShareUserBox.Text = s?.ShareUsername ?? string.Empty;
        SharePassBox.Password = string.Empty;
        SharePassBox.PlaceholderText = !HasSecret("sharePassword")
            ? Loc.Get("Storage_PasswordPlaceholder")
            : Loc.Get("Storage_KeepExisting");

        // SFTP
        HostBox.Text = s?.Host ?? string.Empty;
        PortBox.Text = (s?.Port ?? 22).ToString();
        UserBox.Text = s?.Username ?? string.Empty;
        RemoteDirBox.Text = s?.RemoteDirectory ?? ".";
        var keyAuth = HasSecret("privateKey");
        AuthKey.IsChecked = keyAuth;
        AuthPassword.IsChecked = !keyAuth;
        PassBox.Password = string.Empty;
        KeyPemBox.Text = string.Empty;
        KeyPassBox.Password = string.Empty;
        PassBox.PlaceholderText = !HasSecret("password") ? Loc.Get("Storage_PasswordPlaceholder") : Loc.Get("Storage_KeepExisting");
        KeyPemBox.PlaceholderText = !HasSecret("privateKey")
            ? Loc.Get("Storage_KeyPemPlaceholder")
            : Loc.Get("Storage_KeepExistingKey");
        KeyPassBox.PlaceholderText = !HasSecret("keyPassphrase")
            ? Loc.Get("Storage_KeyPassPlaceholder")
            : Loc.Get("Storage_KeepExistingKeyPass");

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
        status.Text = connected ? Loc.Get("Storage_AccountConnected") : Loc.Get("Storage_AccountNotConnected");
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
        SetStatus(Loc.Get("Storage_OpeningBrowserGoogle"), ok: true, dim: true);
        try
        {
            var token = await RunOAuthLoginAsync(GoogleDriveStorage.AuthorizeAsync);
            if (token is null)
            {
                SetStatus(Loc.Get("Storage_LoginCancelled"), ok: true, dim: true);
                return;
            }
            _pendingGDriveToken = token;
            SetOAuthStatus(GDriveStatus, connected: true);
            SetStatus(Loc.Get("Storage_LoginDone"), ok: true);
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

    private async void MegaLogin_Click(object sender, RoutedEventArgs e)
    {
        var email = MegaEmailBox.Text.Trim();
        if (email.Length == 0 || MegaPassBox.Password.Length == 0)
        {
            SetStatus(Loc.Get("Storage_MegaNeedCreds"), ok: false);
            return;
        }

        SetBusy(true);
        SetStatus(Loc.Get("Storage_MegaLoggingIn"), ok: true, dim: true);
        try
        {
            // Вход выполняем здесь, в окне (пароль/2FA не покидают GUI) → получаем СЕССИЮ,
            // её и сохраним секретом. Дальше служба ходит по сессии без пароля и 2FA.
            var mfa = MegaMfaBox.Text.Trim();
            _pendingMegaToken = await MegaSession.ConnectAsync(email, MegaPassBox.Password, mfa.Length > 0 ? mfa : null);
            SetOAuthStatus(MegaStatus, connected: true);
            SetStatus(Loc.Get("Storage_LoginDone"), ok: true);
            MegaPassBox.Password = string.Empty; // пароль больше не нужен — храним сессию
            MegaMfaBox.Text = string.Empty;
        }
        catch (Exception ex)
        {
            SetOAuthStatus(MegaStatus, connected: false);
            var msg = ex.Message.Contains("TwoFactor", StringComparison.OrdinalIgnoreCase)
                ? Loc.Get("Storage_MegaNeed2Fa")
                : ex.Message;
            SetStatus("✕ " + msg, ok: false);
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
        SetStatus(Loc.Get("Storage_OpeningBrowserDropbox"), ok: true, dim: true);
        try
        {
            var token = await RunOAuthLoginAsync(DropboxStorage.AuthorizeAsync);
            if (token is null)
            {
                SetStatus(Loc.Get("Storage_LoginCancelled"), ok: true, dim: true);
                return;
            }
            _pendingDropboxToken = token;
            SetOAuthStatus(DropboxStatus, connected: true);
            SetStatus(Loc.Get("Storage_LoginDone"), ok: true);
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
            SetStatus(Loc.Get("Storage_NeedPathFirst"), ok: false);
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
            SetStatus(Loc.Get("Storage_OpenFailed", ex.Message), ok: false);
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
    /// Собирает StorageInput из формы для отправки в службу: несекретные поля → Settings,
    /// введённые секреты → PlaintextSecrets (служба перешифрует машинным ключом). Пустое поле
    /// секрета при редактировании = «оставить прежний» (секрет не передаём — служба сохранит
    /// уже имеющийся). null + error — провал валидации.
    /// </summary>
    private StorageInput? BuildInputFromForm(string id, string name, out string? error)
    {
        error = null;
        var settings = new Dictionary<string, string>();
        var secrets = new Dictionary<string, string>();

        StorageInput Make() => new()
        {
            Id = id,
            Name = name,
            Kind = _editorKind.ToString(),
            Settings = settings,
            PlaintextSecrets = secrets.Count > 0 ? secrets : null,
        };

        if (_editorKind == StorageKind.LocalFolder)
        {
            var path = PathBox.Text.Trim();
            if (path.Length == 0) { error = Loc.Get("Storage_ErrNeedPath"); return null; }
            settings["path"] = path;
            var shareUser = ShareUserBox.Text.Trim();
            if (shareUser.Length > 0) settings["shareUsername"] = shareUser;
            if (SharePassBox.Password.Length > 0) secrets["sharePassword"] = SharePassBox.Password;
            return Make();
        }

        if (_editorKind == StorageKind.GoogleDrive)
        {
            if (_pendingGDriveToken is null && !HasSecret("oauthToken"))
            {
                error = Loc.Get("Storage_ErrLoginGoogle");
                return null;
            }
            if (_pendingGDriveToken is not null) secrets["oauthToken"] = _pendingGDriveToken;
            settings["remoteDirectory"] = GDriveFolderBox.Text.Trim();
            return Make();
        }

        if (_editorKind == StorageKind.Dropbox)
        {
            if (_pendingDropboxToken is null && !HasSecret("oauthToken"))
            {
                error = Loc.Get("Storage_ErrLoginDropbox");
                return null;
            }
            if (_pendingDropboxToken is not null) secrets["oauthToken"] = _pendingDropboxToken;
            settings["remoteDirectory"] = DropboxFolderBox.Text.Trim();
            return Make();
        }

        if (_editorKind == StorageKind.WebDav)
        {
            var davUrl = WebDavUrlBox.Text.Trim();
            var davUser = WebDavUserBox.Text.Trim();
            if (davUrl.Length == 0 || davUser.Length == 0) { error = Loc.Get("Storage_ErrNeedUrlUser"); return null; }
            if (WebDavPassBox.Password.Length > 0) secrets["password"] = WebDavPassBox.Password;
            else if (!HasSecret("password")) { error = Loc.Get("Storage_ErrNeedPassword"); return null; }
            settings["serviceUrl"] = davUrl;
            settings["username"] = davUser;
            settings["remoteDirectory"] = WebDavDirBox.Text.Trim();
            return Make();
        }

        if (_editorKind == StorageKind.S3)
        {
            var url = S3UrlBox.Text.Trim();
            var bucket = S3BucketBox.Text.Trim();
            var access = S3AccessBox.Text.Trim();
            if (url.Length == 0 || bucket.Length == 0 || access.Length == 0)
            {
                error = Loc.Get("Storage_ErrNeedS3Fields");
                return null;
            }
            if (S3SecretBox.Password.Length > 0) secrets["secretKey"] = S3SecretBox.Password;
            else if (!HasSecret("secretKey")) { error = Loc.Get("Storage_ErrNeedSecretKey"); return null; }
            settings["serviceUrl"] = url;
            settings["bucket"] = bucket;
            settings["accessKeyId"] = access;
            settings["remoteDirectory"] = S3PrefixBox.Text.Trim();
            settings["forcePathStyle"] = (S3PathStyleCheck.IsChecked == true).ToString();
            return Make();
        }

        if (_editorKind == StorageKind.Ftp)
        {
            var ftpHost = FtpHostBox.Text.Trim();
            var ftpUser = FtpUserBox.Text.Trim();
            if (ftpHost.Length == 0 || ftpUser.Length == 0) { error = Loc.Get("Storage_ErrNeedHostUser"); return null; }

            int ftpPort;
            var ftpPortText = FtpPortBox.Text.Trim();
            if (ftpPortText.Length == 0) ftpPort = 21;
            else if (!int.TryParse(ftpPortText, out ftpPort) || ftpPort is <= 0 or > 65535)
            {
                error = Loc.Get("Storage_ErrPortRange");
                return null;
            }

            if (FtpPassBox.Password.Length > 0) secrets["password"] = FtpPassBox.Password;
            else if (!HasSecret("password")) { error = Loc.Get("Storage_ErrNeedPassword"); return null; }

            settings["host"] = ftpHost;
            settings["port"] = ftpPort.ToString();
            settings["username"] = ftpUser;
            settings["remoteDirectory"] = FtpDirBox.Text.Trim() is { Length: > 0 } d ? d : ".";
            settings["useFtps"] = (FtpsCheck.IsChecked == true).ToString();
            settings["allowUntrustedCertificate"] = (FtpAllowUntrustedCheck.IsChecked == true).ToString();
            return Make();
        }

        if (_editorKind == StorageKind.Mega)
        {
            // Аутентификация — по сессии (кнопка «Войти»), не по паролю: пароль/2FA не хранятся.
            if (_pendingMegaToken is null && !HasSecret("oauthToken"))
            {
                error = Loc.Get("Storage_ErrLoginMega");
                return null;
            }
            if (_pendingMegaToken is not null) secrets["oauthToken"] = _pendingMegaToken;
            settings["username"] = MegaEmailBox.Text.Trim();
            settings["remoteDirectory"] = MegaDirBox.Text.Trim();
            return Make();
        }

        // SFTP
        var host = HostBox.Text.Trim();
        var user = UserBox.Text.Trim();
        if (host.Length == 0 || user.Length == 0) { error = Loc.Get("Storage_ErrNeedHostUser"); return null; }

        int port;
        var portText = PortBox.Text.Trim();
        if (portText.Length == 0) port = 22;
        else if (!int.TryParse(portText, out port) || port is <= 0 or > 65535)
        {
            error = Loc.Get("Storage_ErrPortRange");
            return null;
        }

        if (AuthKey.IsChecked == true)
        {
            if (KeyPemBox.Text.Trim().Length > 0) secrets["privateKey"] = KeyPemBox.Text;
            else if (!HasSecret("privateKey")) { error = Loc.Get("Storage_ErrNeedPrivateKey"); return null; }
            if (KeyPassBox.Password.Length > 0) secrets["keyPassphrase"] = KeyPassBox.Password;
        }
        else
        {
            if (PassBox.Password.Length > 0) secrets["password"] = PassBox.Password;
            else if (!HasSecret("password")) { error = Loc.Get("Storage_ErrNeedPassword"); return null; }
        }

        // Нормализация пути: SFTP не разворачивает «~», пути относительны домашней папки.
        var dir = RemoteDirBox.Text.Trim().Replace('\\', '/');
        if (dir == "~") dir = ".";
        else if (dir.StartsWith("~/", StringComparison.Ordinal)) dir = dir[2..];

        settings["host"] = host;
        settings["port"] = port.ToString();
        settings["username"] = user;
        settings["remoteDirectory"] = dir.Length == 0 ? "." : dir;
        return Make();
    }

    // ---------- действия ----------

    private async void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var input = BuildInputFromForm(_editing?.Id ?? "test", NameBox.Text.Trim(), out var error);
            if (input is null)
            {
                SetStatus(error!, ok: false);
                return;
            }

            var client = await ConnectAsync();
            if (client is null) { SetStatus("✕ " + ServiceError, ok: false); return; }

            SetStatus(Loc.Get("Storage_Testing"), ok: true, dim: true);
            var result = await client.TestStorageAsync(new TestStorageRequest { Inline = input });
            SetStatus(result.Ok ? "✓ " + result.Message : "✕ " + result.Message, result.Ok);
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
                    StorageKind.Mega => "MEGA",
                    _ => HostBox.Text.Trim()
                };

            var client = await ConnectAsync();
            if (client is null) { SetStatus("✕ " + ServiceError, ok: false); return; }

            var id = _editing?.Id ?? MakeId(name, _storages);
            var input = BuildInputFromForm(id, name, out var error);
            if (input is null)
            {
                SetStatus(error!, ok: false);
                return;
            }

            await client.UpsertStorageAsync(input);
            await ReloadAsync(selectId: id);
            SetStatus(Loc.Get("Storage_Saved"), ok: true);

            var item = _items.FirstOrDefault(i => i.Storage.Id == id);
            if (item is not null)
                _ = SelfTestOneAsync(item);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.Get("Storage_SaveFailed", ex.Message), ok: false);
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
        _pendingMegaToken = null;
        ResetTransientUi();
        Editor.Visibility = Visibility.Collapsed;
        EmptyHint.Text = Loc.Get("Storage_EmptyHintText");
        EmptyHint.Visibility = Visibility.Visible;
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null)
            return;

        var appRes = Application.Current.Resources;
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Storage_DeleteDialogTitle"),
            Content = Loc.Get("Storage_DeleteDialogBody", _editing.Name),
            PrimaryButtonText = Loc.Get("Storage_DeleteDialogConfirm"),
            CloseButtonText = Loc.Get("Storage_DeleteDialogCancel"),
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
            var client = await ConnectAsync();
            if (client is null) { SetStatus("✕ " + ServiceError, ok: false); return; }
            await client.DeleteStorageAsync(_editing.Id);
            _editing = null;
            await ReloadAsync(selectId: null);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.Get("Storage_DeleteFailed", ex.Message), ok: false);
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
            var client = await ConnectAsync();
            if (client is null) throw new InvalidOperationException(ServiceError);
            var result = await client.TestStorageAsync(new TestStorageRequest { StorageId = item.Storage.Id });
            item.StatusGlyph = result.Ok ? "✓" : "✕";
            item.StatusBrush = (Brush)Application.Current.Resources[result.Ok ? "EbOkBrush" : "EbErrBrush"];
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

    // Секреты SFTP теперь в службе (под SYSTEM) — клиентский обзор удалённых папок переедет
    // отдельной серверной операцией. Пока путь вводится вручную.
    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
        => SetStatus(Loc.Get("Storage_BrowseUnavailable"),
            ok: true, dim: true);

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
            SetStatus(Loc.Get("Storage_FolderOpenFailed", ex.Message), ok: false);
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
        MegaLoginBtn.IsEnabled = !busy;
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
