using eBackup.Ipc.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace eBackup.App.Pages;

/// <summary>Элемент списка запусков (данные из службы — прогон уже завершён или прерван).</summary>
public sealed class RunItem(BackupRunRecordDto run)
{
    public BackupRunRecordDto Run { get; } = run;

    public string Title => Run.StartedAt.ToString("dd.MM.yyyy HH:mm:ss");

    public string Subtitle => (Run.ArchiveName ?? "архив не собран") + " · " + Run.Trigger;

    public string StatusGlyph => Run.Success switch
    {
        true => Run.SkippedFiles > 0 ? "⚠" : "✓",
        false => "✕",
        null => "⏸"
    };

    public Brush StatusBrush =>
        Run.Success == true && Run.SkippedFiles > 0
            ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF5, 0xB7, 0x4D)) // янтарный — есть пропуски
            : (Brush)Application.Current.Resources[Run.Success switch
            {
                true => "EbOkBrush",
                false => "EbErrBrush",
                null => "EbTextDimBrush"
            }];
}

/// <summary>История бэкапов (из службы): список запусков + полный лог каждого с таймкодами.</summary>
public sealed partial class HistoryPage : Page
{
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

        var client = await ServiceConnection.GetClientAsync();
        if (client is null)
        {
            RunList.ItemsSource = null;
            EmptyHint.Text = "Служба eBackup недоступна: " + (ServiceConnection.Shared.Error ?? "");
            EmptyHint.Visibility = Visibility.Visible;
            Detail.Visibility = Visibility.Collapsed;
            return;
        }

        BackupRunRecordDto[] runs;
        try
        {
            runs = await client.ListHistoryAsync(300);
        }
        catch (Exception ex)
        {
            RunList.ItemsSource = null;
            EmptyHint.Text = "Не удалось прочитать историю службы:\n" + ex.Message;
            EmptyHint.Visibility = Visibility.Visible;
            Detail.Visibility = Visibility.Collapsed;
            return;
        }

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

    private async void RunList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
            $"Модули: {(run.Modules.Length > 0 ? string.Join(", ", run.Modules) : "—")}\n" +
            $"Цели: {(run.Targets.Length > 0 ? string.Join(", ", run.Targets) : "—")}" +
            (run.SizeBytes > 0 ? $"\nРазмер архива: {run.SizeBytes / 1024.0 / 1024.0:0.#} МБ" : "");

        (DetailStatus.Text, DetailStatus.Foreground) = run.Success switch
        {
            true when run.SkippedFiles > 0 => (
                $"⚠ выполнен, но пропущено файлов: {run.SkippedFiles} (нет доступа/заняты)",
                (Brush)new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF5, 0xB7, 0x4D))),
            true => ("✓ выполнен успешно",
                (Brush)Application.Current.Resources["EbOkBrush"]),
            false => ("✕ " + (run.Error ?? "завершён с ошибками"),
                (Brush)Application.Current.Resources["EbErrBrush"]),
            null => ("⏸ не завершён (прерван или служба/приложение перезапущены)",
                (Brush)Application.Current.Resources["EbTextDimBrush"])
        };

        // Лог тянем из службы по runId (прогон выполняла она).
        LogText.Text = "загружаю лог…";
        try
        {
            var client = await ServiceConnection.GetClientAsync();
            if (client is null)
            {
                LogText.Text = "служба недоступна — лог не получить";
                return;
            }
            var resp = await client.GetRunLogAsync(run.Id, 0, 5000);
            if (_selected?.Run.Id != run.Id)
                return; // пока грузили, выбрали другой запуск
            LogText.Text = resp.Lines.Length > 0
                ? string.Join("\n", resp.Lines.Select(l => l.Text))
                : "лог этого запуска не сохранился";
        }
        catch (Exception ex)
        {
            if (_selected?.Run.Id == run.Id)
                LogText.Text = "не удалось получить лог: " + ex.Message;
        }
    }

    /// <summary>Скопировать полный лог выбранного запуска в буфер обмена (лог-файл службы — SYSTEM-only).</summary>
    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || string.IsNullOrEmpty(LogText.Text))
            return;
        try
        {
            var dp = new DataPackage();
            dp.SetText(LogText.Text);
            Clipboard.SetContent(dp);
        }
        catch
        {
            // буфер обмена иногда занят другим процессом — не критично
        }
    }
}
