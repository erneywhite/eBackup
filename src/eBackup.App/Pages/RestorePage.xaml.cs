using System.IO.Compression;
using System.Text.Json;
using eBackup.Core.Abstractions;
using eBackup.Core.Crypto;
using eBackup.Core.Engine;
using eBackup.Core.Model;
using eBackup.Core.Modules;
using eBackup.Modules.Obs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace eBackup.App.Pages;

/// <summary>Откуда брать архив: локальный путь либо имя файла на сохранённом подключении.</summary>
public sealed record RestoreSource(string? LocalPath, string? ConnectionId, string? RemoteName);

public sealed partial class RestorePage : Page
{
    private readonly ModuleRegistry _registry = new(
    [
        new BuiltInModuleSource([new ObsBackupModule()]),
        new DeclarativeModuleSource(),
    ]);

    private RestoreSource? _source;
    private bool _knownEncrypted; // для локального файла известно сразу

    public RestorePage()
    {
        InitializeComponent();
        AssetsDirBox.Text = DefaultAssetsDir();
    }

    private static string DefaultAssetsDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "eBackup", "Assets");

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _source = e.Parameter as RestoreSource;
        if (_source is null)
        {
            ArchiveName.Text = "архив не выбран";
            ArchiveSourceText.Text = "открой страницу «Архивы» и нажми «Восстановить» на нужном архиве";
            LaunchBtn.IsEnabled = false;
            return;
        }

        if (_source.LocalPath is not null)
        {
            ArchiveName.Text = Path.GetFileName(_source.LocalPath);
            ArchiveSourceText.Text = "локальный файл: " + _source.LocalPath;
            try
            {
                _knownEncrypted = ArchiveCipher.IsEncrypted(_source.LocalPath);
            }
            catch
            {
                _knownEncrypted = false;
            }
            PassHint.Text = _knownEncrypted
                ? "Архив зашифрован — для восстановления нужна парольная фраза."
                : "Архив не зашифрован — фраза не нужна.";
            PassBox.Visibility = _knownEncrypted ? Visibility.Visible : Visibility.Collapsed;

            UpdateAssetsCardVisibility();
        }
        else
        {
            ArchiveName.Text = _source.RemoteName ?? "?";
            ArchiveSourceText.Text = "с сервера (скачается во временную папку перед распаковкой)";
            PassHint.Text = "Если архив зашифрован — введи парольную фразу (узнаем после скачивания).";
            PassBox.Visibility = Visibility.Visible;
        }
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
        else
            MainWindow.Instance?.SelectNav("archives");
    }

    /// <summary>
    /// Поле ассетов показываем по СОДЕРЖИМОМУ архива: только если внутри есть записи,
    /// управляемые модулями (внешние ассеты). Для зашифрованных/серверных архивов
    /// заранее не узнать — поле остаётся видимым.
    /// </summary>
    private void UpdateAssetsCardVisibility()
    {
        if (_source?.LocalPath is null || _knownEncrypted)
            return;

        try
        {
            using var zip = ZipFile.OpenRead(_source.LocalPath);
            var entry = zip.GetEntry("manifest.json");
            if (entry is null)
                return;

            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<Manifest>(stream, ManifestJson.Options);
            var hasManagedAssets = manifest?.Modules
                .Any(m => m.Entries.Any(en => en.ManagedByModule)) == true;

            AssetsCard.Visibility = hasManagedAssets ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            // не смогли заглянуть в архив — оставляем поле на месте
        }
    }

    private void DestMode_Changed(object sender, RoutedEventArgs e)
    {
        if (TargetDirBox is null || PickTargetBtn is null)
            return;
        var custom = FolderRadio.IsChecked == true;
        TargetDirBox.IsEnabled = custom;
        PickTargetBtn.IsEnabled = custom;
    }

    private async void PickTargetBtn_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null)
            TargetDirBox.Text = folder;
    }

    private async void PickAssetsBtn_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null)
            AssetsDirBox.Text = folder;
    }

    private static async Task<string?> PickFolderAsync()
    {
        if (MainWindow.Instance is null)
            return null;
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async void LaunchBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null || MainWindow.Instance is null || MainWindow.Instance.IsBusy)
            return;

        string? err = null;
        var customDir = FolderRadio.IsChecked == true;
        if (customDir && TargetDirBox.Text.Trim().Length == 0)
            err = "Укажи папку для распаковки.";
        else if (_knownEncrypted && PassBox.Password.Length == 0)
            err = "Архив зашифрован — введи парольную фразу.";

        if (err is not null)
        {
            ErrorText.Text = err;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        ErrorText.Visibility = Visibility.Collapsed;

        var policy = OverwriteRadio.IsChecked == true ? ConflictPolicy.Overwrite
                   : SkipRadio.IsChecked == true ? ConflictPolicy.Skip
                   : ConflictPolicy.BackupExisting;

        // Модули нужны restore-хукам (например, OBS раскладывает ассеты и правит сцены).
        var modules = _registry.Discover()
            .Where(d => d.Problem is null && d.Instance is not null)
            .Select(d => d.Instance!)
            .ToList();

        var assetsDir = AssetsDirBox.Text.Trim();
        if (assetsDir.Length == 0)
            assetsDir = DefaultAssetsDir();

        var request = new RestoreRequest(
            _source,
            modules,
            policy,
            customDir ? TargetDirBox.Text.Trim() : null,
            assetsDir,
            PassBox.Password.Length > 0 ? PassBox.Password : null);

        LaunchBtn.IsEnabled = false;
        try
        {
            await MainWindow.Instance.StartRestoreAsync(request);
        }
        finally
        {
            LaunchBtn.IsEnabled = true;
        }
    }
}
