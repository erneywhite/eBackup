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
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is ListViewItem { Tag: string tag } && PageTitle is not null)
        {
            PageTitle.Text = tag switch
            {
                "modules" => "Модули",
                "storage" => "Хранилища",
                "archives" => "Архивы",
                _ => "Обзор"
            };
        }
    }
}
