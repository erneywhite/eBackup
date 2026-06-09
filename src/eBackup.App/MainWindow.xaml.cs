using eBackup.App.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace eBackup.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "eBackup";

        // Тёмный кастомный тайтлбар в стиле приложения.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragArea);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));

        ContentFrame.Navigate(typeof(OverviewPage));
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Событие может прилететь во время InitializeComponent, когда Frame ещё не создан.
        if (ContentFrame is null || Nav.SelectedItem is not ListViewItem { Tag: string tag })
            return;

        var page = tag switch
        {
            "modules" => typeof(ModulesPage),
            "storage" => typeof(StoragePage),
            "archives" => typeof(ArchivesPage),
            _ => typeof(OverviewPage)
        };

        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }
}
