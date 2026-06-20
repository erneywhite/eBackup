using eBackup.Ipc.Client;
using eBackup.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

public sealed partial class ArchivesPage : Page
{
    private bool _refreshing;

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

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        RefreshBtn.IsEnabled = false;

        try
        {
            Sections.Children.Clear();
            Sections.Opacity = 0; // прячем на время асинхронной загрузки — покажем разом со «стаггером», без двойного мелькания

            var client = await ServiceConnection.GetClientAsync();
            if (client is null)
            {
                AddDim(Loc.Get("Archives_ServiceUnavailable", ServiceConnection.Shared.Error ?? ""));
                return;
            }

            List<SavedStorage> storages;
            try
            {
                storages = (await client.ListStorageDetailsAsync()).Select(ServiceStorage.ToSaved).ToList();
            }
            catch (Exception ex)
            {
                AddDim(Loc.Get("Archives_StoragesReadFailed", ex.Message));
                return;
            }

            if (storages.Count == 0)
            {
                AddDim(Loc.Get("Archives_NoStorages"));
                return;
            }

            // Единый код для папок, SFTP и будущих облаков.
            foreach (var s in storages)
            {
                AddHeader(s.Kind switch
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
                });

                try
                {
                    // Листинг и удаление — через службу (секрет хранилища у неё, под машинным ключом).
                    // includeEncryption: подглядеть 🔒 (вкладка «Архивы»; «Обзор» этого не просит — экономим).
                    var files = await client.ListArchivesAsync(s.Id, includeEncryption: true);
                    if (files.Length == 0)
                    {
                        AddDim(Loc.Get("Archives_NoArchives"));
                        continue;
                    }

                    foreach (var f in files)
                    {
                        var source = new RestoreSource(null, s.Id, f.Name); // всё единообразно через службу
                        AddRow(f.Name,
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
                            () => OpenBrowse(source),
                            encrypted: f.Encrypted);
                    }
                }
                catch (Exception ex)
                {
                    AddDim(Loc.Get("Archives_Unavailable", ex.Message));
                }
            }

            Entrance.Play(Sections); // заголовки и карточки мягко всплывают по очереди
        }
        finally
        {
            Sections.Opacity = 1; // показываем (Entrance уже выставил детям opacity 0 и анимирует их)
            _refreshing = false;
            RefreshBtn.IsEnabled = true;
        }
    }

    // ---------- строительные блоки списка ----------

    private void AddHeader(string text)
        => Sections.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["EbTextDimBrush"],
            Margin = new Thickness(2, 10, 0, 2),
            TextWrapping = TextWrapping.Wrap
        });

    private void AddDim(string text)
        => Sections.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["EbTextDimBrush"],
            Margin = new Thickness(14, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap
        });

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

    private void AddRow(string title, string subtitle, Action? onRestore = null,
        Func<Task>? onDelete = null, Action? onBrowse = null, bool encrypted = false)
    {
        var appRes = Application.Current.Resources;

        var textPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleBlock = new TextBlock
        {
            Text = (encrypted ? "🔒 " : "") + title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        if (encrypted)
            ToolTipService.SetToolTip(titleBlock, Loc.Get("Archives_TipEncrypted"));
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

        Sections.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = (Brush)appRes["EbCardBrush"],
            BorderBrush = (Brush)appRes["EbCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            Child = row
        });
    }
}
