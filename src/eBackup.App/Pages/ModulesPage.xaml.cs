using System.Text.Json;
using eBackup.Core.Abstractions;
using eBackup.Core.Model;
using eBackup.Core.Modules;
using eBackup.Modules.Obs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

/// <summary>
/// Экран «Модули»: карточки из реестра (встроенные + декларативные drop-in),
/// деталь по клику (включая живой список того, что модуль бэкапит), импорт и
/// удаление декларативных дескрипторов.
/// </summary>
public sealed partial class ModulesPage : Page
{
    private readonly ModuleRegistry _registry = new(
    [
        new BuiltInModuleSource([new ObsBackupModule()]),
        new DeclarativeModuleSource(),
    ]);

    private ModuleDescriptor? _selected;
    private bool _suppressToggle;
    private CatalogIndex? _catalog;   // загруженный каталог (для фильтра без повторной загрузки)

    public ModulesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    // ---------- карточки ----------

    private void Refresh()
    {
        RefreshCards();
        _selected = null;
        Detail.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Visible;
    }

    private void RefreshCards()
    {
        CardsPanel.Children.Clear();
        foreach (var d in _registry.Discover())
            CardsPanel.Children.Add(MakeCard(d));

        HintText.Text = "Декларативные модули подключаются файлом *.module.json — кнопкой «Импорт» "
            + $"или вручную в {ModulePaths.ModulesDirectory}";
    }

    private FrameworkElement MakeCard(ModuleDescriptor d)
    {
        var appRes = Application.Current.Resources;

        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock
        {
            Text = d.DisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = d.Source switch
            {
                ModuleSource.BuiltIn => "встроенный модуль",
                ModuleSource.Declarative => "декларативный модуль",
                _ => "внешний модуль"
            },
            FontSize = 12,
            Foreground = (Brush)appRes["EbTextDimBrush"]
        });
        var (statusText, statusBrush) = d.Problem is not null
            ? ("✕ заблокирован", (Brush)appRes["EbErrBrush"])
            : d.Enabled
                ? ("✓ включён", (Brush)appRes["EbOkBrush"])
                : ("⏸ выключен", (Brush)appRes["EbTextDimBrush"]);
        panel.Children.Add(new TextBlock
        {
            Text = statusText,
            FontSize = 12,
            Foreground = statusBrush
        });

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
            Tag = d
        };
        card.Click += async (_, _) => await ShowDetailAsync(d);

        // Удаление декларативных модулей — маленькая корзина в углу карточки (как в архивах).
        // Встроенные модули удалять нельзя — у них корзины нет.
        if (d.Source == ModuleSource.Declarative && d.Origin is not null)
        {
            var del = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 12 },
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
            del.Click += async (_, _) => await DeleteModuleAsync(d);

            var host = new Grid { Margin = new Thickness(0, 0, 12, 12) };
            host.Children.Add(card);
            host.Children.Add(del);
            return host;
        }

        card.Margin = new Thickness(0, 0, 12, 12);
        return card;
    }

    // ---------- деталь ----------

    private async Task ShowDetailAsync(ModuleDescriptor d)
    {
        _selected = d;
        EmptyHint.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;

        DetailTitle.Text = d.DisplayName;
        DetailMeta.Text = $"id: {d.Id} · {(d.Source == ModuleSource.BuiltIn ? "встроенный" : "декларативный")}"
            + (d.Origin is not null && d.Source == ModuleSource.Declarative ? $"\n{d.Origin}" : "");

        DetailStatus.Text = d.Problem is null ? "✓ готов к работе" : "✕ заблокирован: " + d.Problem;
        DetailStatus.Foreground = (Brush)Application.Current.Resources[d.Problem is null ? "EbOkBrush" : "EbErrBrush"];

        // Выключатель — только для рабочих модулей (заблокированные включать нечем).
        _suppressToggle = true;
        EnableToggle.Visibility = d.Problem is null ? Visibility.Visible : Visibility.Collapsed;
        EnableToggle.IsOn = d.Enabled;
        _suppressToggle = false;

        // Живой список того, что модуль соберёт прямо сейчас.
        DetailEntries.Children.Clear();
        if (d.Instance is null)
        {
            AddEntryLine("— (модуль неактивен)", dim: true);
            return;
        }

        AddEntryLine("собираю список…", dim: true);
        try
        {
            var entries = await d.Instance.DiscoverAsync();
            if (!ReferenceEquals(_selected, d))
                return; // пока ждали — выбрали другой модуль

            DetailEntries.Children.Clear();
            if (entries.Count == 0)
            {
                AddEntryLine("— ничего не найдено на этой машине", dim: true);
                return;
            }

            const int maxShown = 40;
            foreach (var entry in entries.Take(maxShown))
            {
                var kind = entry.Type switch
                {
                    PathEntryType.Directory => "📁",
                    PathEntryType.File => "📄",
                    _ => "🗝"
                };
                AddEntryLine($"{kind} {entry.TokenPath}", dim: false);
            }
            if (entries.Count > maxShown)
                AddEntryLine($"… и ещё {entries.Count - maxShown}", dim: true);
        }
        catch (Exception ex)
        {
            DetailEntries.Children.Clear();
            AddEntryLine("✕ не удалось получить список: " + ex.Message, dim: true);
        }
    }

    private void AddEntryLine(string text, bool dim)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        if (dim)
            tb.Foreground = (Brush)Application.Current.Resources["EbTextDimBrush"];
        DetailEntries.Children.Add(tb);
    }

    private async void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || _selected is null)
            return;

        try
        {
            _registry.SetEnabled(_selected.Id, EnableToggle.IsOn);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось сохранить состояние: " + ex.Message);
            return;
        }

        // Обновляем карточки (статусы ✓/⏸), деталь оставляем открытой.
        RefreshCards();
        _selected = _registry.Discover().FirstOrDefault(x => x.Id == _selected.Id) ?? _selected;
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
            // Источник сканирует только *.module.json — поправим имя при необходимости.
            var name = file.Name.EndsWith(".module.json", StringComparison.OrdinalIgnoreCase)
                ? file.Name
                : Path.GetFileNameWithoutExtension(file.Name) + ".module.json";

            Directory.CreateDirectory(ModulePaths.ModulesDirectory);
            File.Copy(file.Path, Path.Combine(ModulePaths.ModulesDirectory, name), overwrite: true);
            Refresh();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось импортировать: " + ex.Message);
        }
    }

    private async Task DeleteModuleAsync(ModuleDescriptor d)
    {
        if (d is not { Source: ModuleSource.Declarative, Origin: not null })
            return;

        var appRes = Application.Current.Resources;
        var dialog = new ContentDialog
        {
            Title = "Удалить модуль?",
            Content = $"Дескриптор «{d.DisplayName}» будет удалён из папки модулей. Архивы, созданные с ним, останутся.",
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
            File.Delete(d.Origin!);
            Refresh();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось удалить: " + ex.Message);
        }
    }

    // ---------- каталог ----------

    private void InstalledTab_Click(object sender, RoutedEventArgs e) => SetCatalogMode(false);

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

        var installed = _registry.Discover().Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in shown)
            CatalogPanel.Children.Add(MakeCatalogCard(m, installed));
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

            // Валидация скачанного: это декларативный модуль с тем же id и корректным форматом.
            var parsed = JsonSerializer.Deserialize<DeclarativeModuleJson>(json, ManifestJson.Options);
            if (parsed is null || !ModuleValidation.IsValidId(parsed.Id)
                || !string.Equals(parsed.Id, m.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "файл модуля не прошёл проверку (id не совпадает или формат неверен)");

            Directory.CreateDirectory(ModulePaths.ModulesDirectory);
            File.WriteAllText(Path.Combine(ModulePaths.ModulesDirectory, m.Id + ".module.json"), json);

            RefreshCards();   // обновить «Установленные»
            RenderCatalog();  // обновить статусы каталога (теперь «✓ установлен»)
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
