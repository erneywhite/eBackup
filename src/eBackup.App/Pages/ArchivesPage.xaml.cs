using eBackup.Ipc.Client;
using eBackup.Ipc.Contracts;
using eBackup.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

public sealed partial class ArchivesPage : Page
{
    private bool _refreshing;
    private int _generation; // номер текущего обновления: ответы прошлых обновлений в новый список не попадают

    public ArchivesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            // Обновляемся сами, когда бэкап завершился, пока страница открыта.
            MainWindow.BackupCompleted += OnBackupCompleted;
            await RefreshAsync();
        };
        Unloaded += (_, _) => MainWindow.BackupCompleted -= OnBackupCompleted;
    }

    private async void OnBackupCompleted() => await RefreshAsync();

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    /// <summary>
    /// Каждое хранилище — своя секция: опрашиваем ПАРАЛЛЕЛЬНО и рисуем по готовности, чтобы быстрая
    /// локальная папка не ждала MEGA/облака. Метки 🔒 — вторым заходом, когда строки уже на экране.
    /// </summary>
    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        RefreshBtn.IsEnabled = false;
        var gen = ++_generation;

        try
        {
            Sections.Children.Clear();

            var client = await ServiceConnection.GetClientAsync();
            if (client is null)
            {
                AddDim(Sections, Loc.Get("Archives_ServiceUnavailable", ServiceConnection.Shared.Error ?? ""));
                return;
            }

            List<SavedStorage> storages;
            try
            {
                storages = (await client.ListStorageDetailsAsync()).Select(ServiceStorage.ToSaved).ToList();
            }
            catch (Exception ex)
            {
                AddDim(Sections, Loc.Get("Archives_StoragesReadFailed", ex.Message));
                return;
            }

            if (storages.Count == 0)
            {
                AddDim(Sections, Loc.Get("Archives_NoStorages"));
                return;
            }

            var loads = new List<Task>();
            foreach (var s in storages)
            {
                var section = new StackPanel { Spacing = 6 };
                AddHeader(section, HeaderFor(s));
                var rows = new StackPanel { Spacing = 6 };
                AddDim(rows, Loc.Get("Archives_Loading"));
                section.Children.Add(rows);
                Sections.Children.Add(section);
                loads.Add(LoadStorageAsync(client, s, rows, gen));
            }
            Entrance.Play(Sections); // заголовки хранилищ мягко всплывают сразу, архивы подтянутся по готовности

            await Task.WhenAll(loads);
        }
        finally
        {
            _refreshing = false;
            RefreshBtn.IsEnabled = true;
        }
    }

    private async Task LoadStorageAsync(IpcClient client, SavedStorage s, StackPanel rows, int gen)
    {
        RemoteFileDto[] files;
        try
        {
            // Листинг и удаление — через службу (секрет хранилища у неё, под машинным ключом).
            files = await client.ListArchivesAsync(s.Id);
        }
        catch (Exception ex)
        {
            if (gen != _generation) return;
            rows.Children.Clear();
            AddDim(rows, Loc.Get("Archives_Unavailable", ex.Message));
            return;
        }
        if (gen != _generation) return;

        rows.Children.Clear();
        if (files.Length == 0)
        {
            AddDim(rows, Loc.Get("Archives_NoArchives"));
            return;
        }

        var titles = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var source = new RestoreSource(null, s.Id, f.Name); // всё единообразно через службу
            titles[f.Name] = AddRow(rows, f.Name,
                Loc.Get("Archives_RowSubtitle", f.Length / 1024.0 / 1024.0, f.LastWriteTime),
                () => OpenRestore(source),
                async () =>
                {
                    if (!await ConfirmDeleteAsync(f.Name, Loc.Get("Archives_DeleteFrom", s.Name)))
                        return;
                    try
                    {
                        await client.DeleteArchiveAsync(s.Id, f.Name);
                    }
                    catch (Exception ex)
                    {
                        await ShowErrorAsync(Loc.Get("Archives_DeleteFailed", ex.Message));
                    }
                    await RefreshAsync();
                },
                () => OpenBrowse(source));
        }
        Entrance.Play(rows);

        // 🔒 — фоном: служба подглядывает заголовки только новых архивов (остальное из её кэша).
        try
        {
            var withFlags = await client.ListArchivesAsync(s.Id, includeEncryption: true);
            if (gen != _generation) return;
            foreach (var f in withFlags.Where(x => x.Encrypted))
                if (titles.TryGetValue(f.Name, out var title))
                    MarkEncrypted(title);
        }
        catch { /* без меток — список уже на экране */ }
    }

    private static string HeaderFor(SavedStorage s) => s.Kind switch
    {
        StorageKind.LocalFolder => $"{s.Name} — {s.Path}",
        StorageKind.Sftp => Loc.Get("Archives_HeaderSftp", s.Name, s.Username, s.Host, s.Port, s.RemoteDirectory),
        StorageKind.Ftp => Loc.Get("Archives_HeaderFtp", s.Name, s.UseFtps ? "ftps" : "ftp", s.Username, s.Host, s.Port, s.RemoteDirectory),
        StorageKind.S3 => $"{s.Name} — s3 · {s.Bucket}"
            + (string.IsNullOrWhiteSpace(s.RemoteDirectory) ? "" : $"/{s.RemoteDirectory!.Trim('/')}"),
        StorageKind.WebDav => $"{s.Name} — webdav · {s.ServiceUrl}"
            + (string.IsNullOrWhiteSpace(s.RemoteDirectory) ? "" : $"/{s.RemoteDirectory!.Trim('/')}"),
        StorageKind.GoogleDrive =>
            Loc.Get("Archives_HeaderGoogleDrive", s.Name, string.IsNullOrWhiteSpace(s.RemoteDirectory) ? "eBackup" : s.RemoteDirectory),
        StorageKind.Dropbox => Loc.Get("Archives_HeaderDropbox", s.Name)
            + (string.IsNullOrWhiteSpace(s.RemoteDirectory) ? "" : $"/{s.RemoteDirectory!.Trim('/')}"),
        _ => s.Name
    };

    // ---------- строительные блоки списка ----------

    private static void AddHeader(Panel target, string text)
        => target.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["EbTextDimBrush"],
            Margin = new Thickness(2, 10, 0, 2),
            TextWrapping = TextWrapping.Wrap
        });

    private static void AddDim(Panel target, string text)
        => target.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["EbTextDimBrush"],
            Margin = new Thickness(14, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap
        });

    private static void MarkEncrypted(TextBlock title)
    {
        if (title.Text.StartsWith("🔒", StringComparison.Ordinal))
            return;
        title.Text = "🔒 " + title.Text;
        ToolTipService.SetToolTip(title, Loc.Get("Archives_TipEncrypted"));
    }

    private void OpenRestore(RestoreSource source)
        => Frame.Navigate(typeof(RestorePage), source);

    private void OpenBrowse(RestoreSource source)
        => Frame.Navigate(typeof(ArchiveBrowsePage), source);

    /// <summary>Подтверждение удаления (диалог в стиле приложения).</summary>
    private async Task<bool> ConfirmDeleteAsync(string name, string where)
    {
        var appRes = Application.Current.Resources;
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Archives_DeleteDialogTitle"),
            Content = Loc.Get("Archives_DeleteDialogContent", name, where),
            PrimaryButtonText = Loc.Get("Archives_DeleteDialogPrimary"),
            CloseButtonText = Loc.Get("Archives_DeleteDialogCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            Background = (Brush)appRes["EbDialogBrush"],
            BorderBrush = (Brush)appRes["EbCardBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            CloseButtonStyle = (Style)appRes["EbDialogCloseStyle"]
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowErrorAsync(string message)
    {
        var appRes = Application.Current.Resources;
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Archives_ErrorDialogTitle"),
            Content = message,
            CloseButtonText = Loc.Get("Archives_ErrorDialogClose"),
            XamlRoot = XamlRoot,
            Background = (Brush)appRes["EbDialogBrush"],
            BorderBrush = (Brush)appRes["EbCardBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            CloseButtonStyle = (Style)appRes["EbDialogCloseStyle"]
        };
        await dialog.ShowAsync();
    }

    /// <summary>Карточка архива; возвращает заголовок, чтобы потом дорисовать 🔒.</summary>
    private static TextBlock AddRow(Panel target, string title, string subtitle, Action? onRestore = null,
        Func<Task>? onDelete = null, Action? onBrowse = null)
    {
        var appRes = Application.Current.Resources;

        var textPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        textPanel.Children.Add(titleBlock);
        textPanel.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = (Brush)appRes["EbTextDimBrush"]
        });

        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textPanel, 0);
        row.Children.Add(textPanel);

        if (onBrowse is not null)
        {
            var browseBtn = new Button
            {
                Content = Loc.Get("Archives_OpenButton"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 6, 14, 6),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(browseBtn, Loc.Get("Archives_TipBrowse"));
            browseBtn.Click += (_, _) => onBrowse();
            Grid.SetColumn(browseBtn, 1);
            row.Children.Add(browseBtn);
        }

        if (onRestore is not null)
        {
            var restoreBtn = new Button
            {
                Content = Loc.Get("Archives_RestoreButton"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 6, 14, 6),
                VerticalAlignment = VerticalAlignment.Center
            };
            restoreBtn.Click += (_, _) => onRestore();
            Grid.SetColumn(restoreBtn, 2);
            row.Children.Add(restoreBtn);
        }

        if (onDelete is not null)
        {
            var deleteBtn = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 13 }, // корзина (Delete)
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 7, 10, 7),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0x8A, 0x9C))
            };
            ToolTipService.SetToolTip(deleteBtn, Loc.Get("Archives_TipDelete"));
            deleteBtn.Click += async (_, _) => await onDelete();
            Grid.SetColumn(deleteBtn, 3);
            row.Children.Add(deleteBtn);
        }

        target.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = (Brush)appRes["EbCardBrush"],
            BorderBrush = (Brush)appRes["EbCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            Child = row
        });
        return titleBlock;
    }
}
