using System.Windows.Input;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>One action's row on the <see cref="HotkeysViewModel"/>.</summary>
internal sealed class HotkeyRow : Observable
{
    private string _bindingText = "Not set";
    private string _statusText = "Not set";
    private bool _isFailed;
    private bool _isCapturing;
    private bool _isShown = true;

    public HotkeyRow(HotkeysViewModel viewModel, HotkeyAction action, string label)
    {
        Action = action;
        Label = label;
        SetCommand = new RelayCommand(() => viewModel.StartCapture(action));
        ClearCommand = new RelayCommand(() => viewModel.Clear(action));
    }

    public HotkeyAction Action { get; }

    public string Label { get; }

    /// <summary>The binding, e.g. "Alt+F10", or a prompt while capturing.</summary>
    public string BindingText => IsCapturing ? "Press a combination… (Esc cancels)" : _bindingText;

    /// <summary>The engine's status for the action, e.g. "Working".</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>The engine failed to register the binding.</summary>
    public bool IsFailed
    {
        get => _isFailed;
        private set => Set(ref _isFailed, value);
    }

    /// <summary>The selected Record mode uses the action; the view hides the row otherwise.</summary>
    public bool IsShown
    {
        get => _isShown;
        private set => Set(ref _isShown, value);
    }

    /// <summary>The row is waiting for a combination.</summary>
    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (Set(ref _isCapturing, value))
            {
                OnPropertyChanged(nameof(BindingText));
            }
        }
    }

    /// <summary>Starts capture on this row, ending it on the others.</summary>
    public ICommand SetCommand { get; }

    public ICommand ClearCommand { get; }

    internal void Show(string bindingText, string statusText, bool isFailed, bool isShown)
    {
        IsShown = isShown;

        // A hidden row can't take the next key press.
        if (!isShown)
        {
            IsCapturing = false;
        }
        if (_bindingText != bindingText)
        {
            _bindingText = bindingText;
            OnPropertyChanged(nameof(BindingText));
        }

        StatusText = statusText;
        IsFailed = isFailed;
    }
}
