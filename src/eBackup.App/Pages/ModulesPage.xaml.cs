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

    private Button MakeCard(ModuleDescriptor d)
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
            Margin = new Thickness(0, 0, 12, 12),
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

        DeleteModuleBtn.Visibility = d.Source == ModuleSource.Declarative && d.Origin is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

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

    private async void DeleteModuleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { Source: ModuleSource.Declarative, Origin: not null } d)
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
