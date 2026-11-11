using System.Windows.Input;

namespace Encore.App;

/// <summary>A command with a parameter.</summary>
internal sealed class ParameterCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
