using System.Windows.Input;

namespace eBackup.App;

/// <summary>Минимальный ICommand для трей-иконки (клик → действие).</summary>
public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
