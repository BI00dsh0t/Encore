using System.Windows.Input;

namespace Encore.App;

/// <summary>A command that runs an action, always or while canExecute is true.</summary>
internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    /// <summary>Runs the action; a no-op while the command can't execute.</summary>
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
