using System.Text.Json;
using eBackup.Core.Model;
using eBackup.Core.Modules;
using eBackup.Ipc.Client;
using eBackup.Ipc.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

/// <summary>
/// Экран «Модули»: карточки из реестра СЛУЖБЫ (встроенные + декларативные drop-in),
/// деталь по клику (включая живой список того, что модуль бэкапит), импорт и удаление
/// декларативных дескрипторов. Реестр службы — источник истины для бэкапов.
/// </summary>
public sealed partial class ModulesPage : Page
{
    private ModuleSummary? _selected;
    private bool _suppressToggle;
    private HashSet<string> _installedIds = new(StringComparer.OrdinalIgnoreCase);
    private CatalogIndex? _catalog;   // загруженный каталог (для фильтра без повторной загрузки)

    public ModulesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private static Task<IpcClient?> ClientAsync() => ServiceConnection.GetClientAsync();

    // ---------- карточки ----------

    private async Task RefreshAsync()
    {
        await RefreshCardsAsync();
        _selected = null;
        Detail.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Visible;
    }

    private async Task RefreshCardsAsync()
    {
        CardsPanel.Children.Clear();
        HintText.Text = "Декларативные модули подключаются файлом *.module.json — кнопкой «Импорт» или из каталога.";

        var client = await ClientAsync();
        if (client is null)
        {
            _installedIds = new(StringComparer.OrdinalIgnoreCase);
            CardsPanel.Children.Add(new TextBlock
            {
                Text = "служба eBackup недоступна: " + (ServiceConnection.Shared.Error ?? ""),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["EbTextDimBrush"]
            });
            return;
        }

        ModuleSummary[] mods;
        try { mods = await client.ListModulesAsync(); }
        catch (Exception ex)
        {
            CardsPanel.Children.Add(new TextBlock
            {
                Text = "не удалось прочитать модули службы: " + ex.Message,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["EbTextDimBrush"]
            });
            return;
        }

        _installedIds = mods.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
            CardsPanel.Children.Add(MakeCard(m));
    }

    private static bool IsDeclarative(ModuleSummary m) => m.Source == "Declarative";

    private FrameworkElement MakeCard(ModuleSummary m)
    {
        var appRes = Application.Current.Resources;

        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock
        {
            Text = m.DisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = m.Source switch
            {
                "BuiltIn" => "встроенный модуль",
                "Declarative" => "декларативный модуль",
                _ => "внешний модуль"
            },
            FontSize = 12,
            Foreground = (Brush)appRes["EbTextDimBrush"]
        });
        var (statusText, statusBrush) = m.Problem is not null
            ? ("✕ заблокирован", (Brush)appRes["EbErrBrush"])
            : m.Enabled
                ? ("✓ включён", (Brush)appRes["EbOkBrush"])
                : ("⏸ выключен", (Brush)appRes["EbTextDimBrush"]);
        panel.Children.Add(new TextBlock { Text = statusText, FontSize = 12, Foreground = statusBrush });

        var card = new Button
        {
            Width = 210,
            Height = 110,
            CornerRadius = new CornerRadius(16),
            Background = (Brush)appRes["EbCardBrush"],
            BorderBrush = (Brush)appRes["EbCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12, 16, 12),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            Content = panel,
            Tag = m
        };
        card.Click += async (_, _) => await ShowDetailAsync(m);

        // Удаление декларативных модулей — маленькая корзина в углу (встроенные не удаляем).
        if (IsDeclarative(m))
        {
            var del = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 12 },
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 5, 7, 5),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0),
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0x8A, 0x9C))
            };
            ToolTipService.SetToolTip(del, "Удалить модуль");
            del.Click += async (_, _) => await DeleteModuleAsync(m);

            var host = new Grid { Margin = new Thickness(0, 0, 12, 12) };
            host.Children.Add(card);
            host.Children.Add(del);
            return host;
        }

        card.Margin = new Thickness(0, 0, 12, 12);
        return card;
    }

    // ---------- деталь ----------

    private async Task ShowDetailAsync(ModuleSummary m)
    {
        _selected = m;
        EmptyHint.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;

        DetailTitle.Text = m.DisplayName;
        DetailMeta.Text = $"id: {m.Id} · {(m.Source == "BuiltIn" ? "встроенный" : "декларативный")}";

        DetailStatus.Text = m.Problem is null ? "✓ готов к работе" : "✕ заблокирован: " + m.Problem;
        DetailStatus.Foreground = (Brush)Application.Current.Resources[m.Problem is null ? "EbOkBrush" : "EbErrBrush"];

        // Выключатель — только для рабочих модулей (заблокированные включать нечем).
        _suppressToggle = true;
        EnableToggle.Visibility = m.Problem is null ? Visibility.Visible : Visibility.Collapsed;
        EnableToggle.IsOn = m.Enabled;
        _suppressToggle = false;

        // Живой список того, что модуль соберёт (token-пути от службы).
        DetailEntries.Children.Clear();
        if (m.Problem is not null)
        {
            AddEntryLine("— (модуль неактивен)", dim: true);
            return;
        }

        AddEntryLine("собираю список…", dim: true);
        try
        {
            var client = await ClientAsync();
            if (client is null) { DetailEntries.Children.Clear(); AddEntryLine("— служба недоступна", dim: true); return; }
            var entries = await client.DiscoverModuleAsync(m.Id);
            if (!ReferenceEquals(_selected, m))
                return; // пока ждали — выбрали другой модуль

            DetailEntries.Children.Clear();
            if (entries.Length == 0)
            {
                AddEntryLine("— ничего не найдено на этой машине", dim: true);
                return;
            }

            const int maxShown = 40;
            foreach (var entry in entries.Take(maxShown))
            {
                var kind = entry.Type switch
                {
                    "Directory" => "📁",
                    "File" => "📄",
                    _ => "🗝"
                };
                AddEntryLine($"{kind} {entry.TokenPath}", dim: false);
            }
            if (entries.Length > maxShown)
                AddEntryLine($"… и ещё {entries.Length - maxShown}", dim: true);
        }
        catch (Exception ex)
        {
            DetailEntries.Children.Clear();
            AddEntryLine("✕ не удалось получить список: " + ex.Message, dim: true);
        }
    }

    private void AddEntryLine(string text, bool dim)
    {
        var tb = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        if (dim)
            tb.Foreground = (Brush)Application.Current.Resources["EbTextDimBrush"];
        DetailEntries.Children.Add(tb);
    }

    private async void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || _selected is not { } sel)
            return;

        var on = EnableToggle.IsOn;
        try
        {
            var client = await ClientAsync();
            if (client is null) throw new InvalidOperationException(ServiceConnection.Shared.Error ?? "служба недоступна");
            await client.SetModuleEnabledAsync(sel.Id, on);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось сохранить состояние: " + ex.Message);
            return;
        }

        // Обновляем карточки (статусы ✓/⏸), деталь оставляем открытой.
        await RefreshCardsAsync();
        _selected = sel with { Enabled = on };
    }

    // ---------- импорт / удаление ----------

    private async void ImportBtn_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null)
            return;

        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        try
        {
            var json = await File.ReadAllTextAsync(file.Path);
            var client = await ClientAsync();
            if (client is null) throw new InvalidOperationException(ServiceConnection.Shared.Error ?? "служба недоступна");
            await client.InstallModuleAsync(json); // служба валидирует и кладёт в свой реестр
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось импортировать: " + ex.Message);
        }
    }

    private async Task DeleteModuleAsync(ModuleSummary m)
    {
        if (!IsDeclarative(m))
            return;

        var appRes = Application.Current.Resources;
        var dialog = new ContentDialog
        {
            Title = "Удалить модуль?",
            Content = $"Дескриптор «{m.DisplayName}» будет удалён из реестра службы. Архивы, созданные с ним, останутся.",
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

        try
        {
            var client = await ClientAsync();
            if (client is null) throw new InvalidOperationException(ServiceConnection.Shared.Error ?? "служба недоступна");
            await client.DeleteModuleAsync(m.Id);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось удалить: " + ex.Message);
        }
    }

    // ---------- каталог ----------

    private async void InstalledTab_Click(object sender, RoutedEventArgs e) { SetCatalogMode(false); await RefreshAsync(); }

    private async void CatalogTab_Click(object sender, RoutedEventArgs e)
    {
        SetCatalogMode(true);
        await LoadCatalogAsync();
    }

    private void SetCatalogMode(bool catalog)
    {
        InstalledTab.IsChecked = !catalog;
        CatalogTab.IsChecked = catalog;
        InstalledRoot.Visibility = catalog ? Visibility.Collapsed : Visibility.Visible;
        CatalogRoot.Visibility = catalog ? Visibility.Visible : Visibility.Collapsed;
        if (catalog)
        {
            // Деталь-панель относится к установленным — в каталоге сбрасываем выбор.
            _selected = null;
            Detail.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Visible;
        }
    }

    private async Task LoadCatalogAsync()
    {
        CatalogPanel.Children.Clear();
        CatalogStatus.Text = "Загружаю каталог…";

        CatalogResult result;
        try
        {
            result = await CatalogService.LoadAsync();
        }
        catch (Exception ex)
        {
            CatalogStatus.Text = "⚠ не удалось загрузить каталог: " + ex.Message;
            return;
        }

        if (result.Index is null)
        {
            CatalogStatus.Text = "⚠ " + (result.Error ?? "каталог недоступен");
            return;
        }

        _catalog = result.Index;
        CatalogStatus.Text = result.Source == CatalogSource.Cache
            ? $"⚠ нет сети — сохранённый список ({_catalog.Modules.Count})"
            : $"Доступно: {_catalog.Modules.Count}";
        RenderCatalog();
    }

    private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderCatalog();

    private void RenderCatalog()
    {
        if (_catalog is null)
            return;

        CatalogPanel.Children.Clear();
        var tag = (CategoryFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        IEnumerable<CatalogModule> shown = tag switch
        {
            "game" => _catalog.Modules.Where(m => m.Category == "game"),
            "app" => _catalog.Modules.Where(m => m.Category == "app"),
            "other" => _catalog.Modules.Where(m => m.Category is not "game" and not "app"),
            _ => _catalog.Modules
        };

        foreach (var m in shown)
            CatalogPanel.Children.Add(MakeCatalogCard(m, _installedIds));
    }

    private FrameworkElement MakeCatalogCard(CatalogModule m, HashSet<string> installedIds)
    {
        var appRes = Application.Current.Resources;
        var dim = (Brush)appRes["EbTextDimBrush"];
        var installed = installedIds.Contains(m.Id);

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = m.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(m.Description))
            panel.Children.Add(new TextBlock
            {
                Text = m.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = dim
            });
        panel.Children.Add(new TextBlock
        {
            Text = $"{CategoryLabel(m.Category)} · трогает: {PrettyTokens(m.Tokens)}",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = dim
        });

        if (installed)
            panel.Children.Add(new TextBlock
            {
                Text = "✓ установлен",
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)appRes["EbOkBrush"]
            });

        var btn = new Button
        {
            Content = installed ? "Обновить" : "Загрузить",
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 6, 0, 0)
        };
        btn.Click += async (_, _) => await InstallFromCatalogAsync(m, btn);
        panel.Children.Add(btn);

        return new Border
        {
            Width = 260,
            CornerRadius = new CornerRadius(16),
            Background = (Brush)appRes["EbCardBrush"],
            BorderBrush = (Brush)appRes["EbCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 12, 12),
            Child = panel
        };
    }

    private async Task InstallFromCatalogAsync(CatalogModule m, Button btn)
    {
        btn.IsEnabled = false;
        btn.Content = "Загружаю…";
        try
        {
            var json = await CatalogService.DownloadModuleJsonAsync(m);

            // Валидация скачанного ДО отправки: декларативный модуль с тем же id и корректным форматом.
            var parsed = JsonSerializer.Deserialize<DeclarativeModuleJson>(json, ManifestJson.Options);
            if (parsed is null || !ModuleValidation.IsValidId(parsed.Id)
                || !string.Equals(parsed.Id, m.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "файл модуля не прошёл проверку (id не совпадает или формат неверен)");

            var client = await ClientAsync();
            if (client is null) throw new InvalidOperationException(ServiceConnection.Shared.Error ?? "служба недоступна");
            await client.InstallModuleAsync(json); // служба перепроверит и положит в свой реестр

            await RefreshCardsAsync(); // обновить «Установленные» + _installedIds
            RenderCatalog();           // обновить статусы каталога (теперь «✓ установлен»)
        }
        catch (Exception ex)
        {
            btn.IsEnabled = true;
            btn.Content = "Загрузить";
            await ShowErrorAsync("Не удалось установить модуль: " + ex.Message);
        }
    }

    private static string CategoryLabel(string? category) => category switch
    {
        "game" => "игра",
        "app" => "софт",
        "server" => "сервер",
        _ => "модуль"
    };

    private static string PrettyTokens(List<string> tokens)
        => tokens.Count == 0
            ? "—"
            : string.Join(", ", tokens.Select(t => t.Replace("{", "%").Replace("}", "%")));

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
}
