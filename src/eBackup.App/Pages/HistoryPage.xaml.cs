using eBackup.Core.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace eBackup.App.Pages;

/// <summary>Элемент списка запусков (значения статичны — запись уже завершена или прервана).</summary>
public sealed class RunItem(BackupRunRecord run)
{
    public BackupRunRecord Run { get; } = run;

    public string Title => Run.StartedAt.ToString("dd.MM.yyyy HH:mm:ss");

    public string Subtitle => (Run.ArchiveName ?? "архив не собран") + " · " + Run.Trigger;

    public string StatusGlyph => Run.Success switch
    {
        true => "✓",
        false => "✕",
        null => "⏸"
    };

    public Brush StatusBrush => (Brush)Application.Current.Resources[Run.Success switch
    {
        true => "EbOkBrush",
        false => "EbErrBrush",
        null => "EbTextDimBrush"
    }];
}

/// <summary>История бэкапов: список запусков + полный лог каждого с таймкодами.</summary>
public sealed partial class HistoryPage : Page
{
    private readonly HistoryStore _store = new();
    private RunItem? _selected;

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            MainWindow.BackupCompleted += OnBackupCompleted;
            await ReloadAsync();
        };
        Unloaded += (_, _) => MainWindow.BackupCompleted -= OnBackupCompleted;
    }

    private async void OnBackupCompleted() => await ReloadAsync();

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        var selectedId = _selected?.Run.Id;
        var runs = await _store.LoadAsync();
        var items = runs.Select(r => new RunItem(r)).ToList();
        RunList.ItemsSource = items;

        if (items.Count == 0)
        {
            EmptyHint.Text = "Запусков ещё не было — сделай первый бэкап, и здесь появится его журнал";
            EmptyHint.Visibility = Visibility.Visible;
            Detail.Visibility = Visibility.Collapsed;
            return;
        }

        // Сохраняем выбор после обновления; иначе ничего не выбрано — подсказка.
        var keep = selectedId is null ? null : items.FirstOrDefault(i => i.Run.Id == selectedId);
        if (keep is not null)
            RunList.SelectedItem = keep;
    }

    private void RunList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RunList.SelectedItem is not RunItem item)
            return;

        _selected = item;
        var run = item.Run;

        EmptyHint.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;

        DetailTitle.Text = run.ArchiveName ?? $"Запуск {run.StartedAt:dd.MM.yyyy HH:mm:ss}";

        var duration = run.FinishedAt is null
            ? "не завершён"
            : (run.FinishedAt.Value - run.StartedAt).ToString(@"mm\:ss");
        DetailMeta.Text =
            $"Запуск: {run.Trigger}\n" +
            $"Начало: {run.StartedAt:dd.MM.yyyy HH:mm:ss} · длительность: {duration}\n" +
            $"Модули: {(run.Modules.Count > 0 ? string.Join(", ", run.Modules) : "—")}\n" +
            $"Цели: {(run.Targets.Count > 0 ? string.Join(", ", run.Targets) : "—")}" +
            (run.SizeBytes > 0 ? $"\nРазмер архива: {run.SizeBytes / 1024.0 / 1024.0:0.#} МБ" : "");

        (DetailStatus.Text, DetailStatus.Foreground) = run.Success switch
        {
            true => ("✓ выполнен успешно",
                (Brush)Application.Current.Resources["EbOkBrush"]),
            false => ("✕ " + (run.Error ?? "завершён с ошибками"),
                (Brush)Application.Current.Resources["EbErrBrush"]),
            null => ("⏸ не завершён (прерван или приложение было закрыто)",
                (Brush)Application.Current.Resources["EbTextDimBrush"])
        };

        var log = _store.ReadLog(run.Id);
        LogText.Text = log.Length > 0 ? log : "лог этого запуска не сохранился";
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;
        try
        {
            var path = _store.LogPath(_selected.Run.Id);
            if (!File.Exists(path))
                return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // не критично
        }
    }
}
