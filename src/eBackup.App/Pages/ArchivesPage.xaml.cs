using eBackup.Core.Crypto;
using eBackup.Security;
using eBackup.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

public sealed partial class ArchivesPage : Page
{
    private readonly StorageStore _store = new(new DpapiSecretProtector());
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

            List<SavedStorage> storages;
            try
            {
                storages = (await _store.LoadAsync()).ToList();
            }
            catch (Exception ex)
            {
                AddDim("не удалось прочитать конфиг хранилищ: " + ex.Message);
                return;
            }

            if (storages.Count == 0)
            {
                AddDim("хранилищ нет — добавь на странице «Хранилища», и архивы появятся здесь");
                return;
            }

            // Единый код для папок, SFTP и будущих облаков.
            foreach (var s in storages)
            {
                AddHeader(s.Kind switch
                {
                    StorageKind.LocalFolder => $"{s.Name} — {s.Path}",
                    StorageKind.Sftp => $"{s.Name} — {s.Username}@{s.Host}:{s.Port}, папка {s.RemoteDirectory}",
                    _ => s.Name
                });

                try
                {
                    var storage = StorageFactory.Create(s, _store.Protector);
                    var files = await storage.ListDetailedAsync();
                    if (files.Count == 0)
                    {
                        AddDim("архивов нет");
                        continue;
                    }

                    foreach (var f in files)
                    {
                        var encrypted = false;
                        string? directPath = null;
                        if (storage is FolderStorage folder)
                        {
                            directPath = folder.GetLocalPath(f.Name);
                            try { encrypted = ArchiveCipher.IsEncrypted(directPath); } catch { }
                        }

                        var source = directPath is not null
                            ? new RestoreSource(directPath, null, null)
                            : new RestoreSource(null, s.Id, f.Name);

                        AddRow(f.Name,
                            $"{f.Length / 1024.0 / 1024.0:0.#} МБ · {f.LastWriteTime:dd.MM.yyyy HH:mm}"
                            + (encrypted ? " · 🔒 зашифрован" : ""),
                            () => OpenRestore(source),
                            async () =>
                            {
                                if (!await ConfirmDeleteAsync(f.Name, $"из «{s.Name}»"))
                                    return;
                                try
                                {
                                    await storage.DeleteAsync(f.Name);
                                }
                                catch (Exception ex)
                                {
                                    await ShowErrorAsync("Не удалось удалить: " + ex.Message);
                                }
                                await RefreshAsync();
                            });
                    }
                }
                catch (Exception ex)
                {
                    AddDim("✕ недоступно: " + ex.Message);
                }
            }
        }
        finally
        {
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

    /// <summary>Подтверждение удаления (диалог в стиле приложения).</summary>
    private async Task<bool> ConfirmDeleteAsync(string name, string where)
    {
        var appRes = Application.Current.Resources;
        var dialog = new ContentDialog
        {
            Title = "Удалить архив?",
            Content = $"«{name}» будет удалён {where}. Действие необратимо.",
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
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowErrorAsync(string message)
    {
        var appRes = Application.Current.Resources;
        var dialog = new ContentDialog
        {
            Title = "Ошибка",
            Content = message,
            CloseButtonText = "Понятно",
            XamlRoot = XamlRoot,
            Background = (Brush)appRes["EbDialogBrush"],
            BorderBrush = (Brush)appRes["EbCardBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            CloseButtonStyle = (Style)appRes["EbDialogCloseStyle"]
        };
        await dialog.ShowAsync();
    }

    private void AddRow(string title, string subtitle, Action? onRestore = null, Func<Task>? onDelete = null)
    {
        var appRes = Application.Current.Resources;

        var textPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
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
        Grid.SetColumn(textPanel, 0);
        row.Children.Add(textPanel);

        if (onRestore is not null)
        {
            var restoreBtn = new Button
            {
                Content = "Восстановить",
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 6, 14, 6),
                VerticalAlignment = VerticalAlignment.Center
            };
            restoreBtn.Click += (_, _) => onRestore();
            Grid.SetColumn(restoreBtn, 1);
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
            ToolTipService.SetToolTip(deleteBtn, "Удалить архив");
            deleteBtn.Click += async (_, _) => await onDelete();
            Grid.SetColumn(deleteBtn, 2);
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
