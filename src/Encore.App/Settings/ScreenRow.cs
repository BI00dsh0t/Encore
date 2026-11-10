using Encore.Contracts.Messages;

namespace Encore.App.Settings;

/// <summary>One screen's row in the <see cref="MonitorPicker"/>: a check box with its number, name, mode, primary and HDR.</summary>
internal sealed class ScreenRow(MonitorPicker picker, string id) : Observable
{
    private string _label = "";
    private bool _isChosen;
    private bool _isEnabled = true;
    private string? _toolTip;
    private DisplayMonitor? _monitor;

    /// <summary>The id the selection stores for this screen.</summary>
    public string Id { get; } = id;

    /// <summary>The attached screen; null for a chosen screen that isn't attached.</summary>
    public DisplayMonitor? Monitor => _monitor;

    /// <summary>"1  Dell U2723QE · 2560 × 1440 · 144 Hz · primary", or "… · Not connected — skipped".</summary>
    public string Label
    {
        get => _label;
        private set => Set(ref _label, value);
    }

    /// <summary>The screen is attached; a chosen one that isn't shows greyed.</summary>
    public bool IsConnected => _monitor is not null;

    /// <summary>Ticked: recorded. Setting it ticks or unticks the screen through the picker.</summary>
    public bool IsChosen
    {
        get => _isChosen;
        set
        {
            if (value != _isChosen)
            {
                picker.Toggle(this, value);
            }
        }
    }

    /// <summary>Chosen screens: the builder can tick it. All screens shows every screen ticked and disabled.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        private set => Set(ref _isEnabled, value);
    }

    /// <summary>Why the last chosen screen can't be unticked; null otherwise.</summary>
    public string? ToolTip
    {
        get => _toolTip;
        private set => Set(ref _toolTip, value);
    }

    /// <summary>Shows <see cref="IsChosen"/> again, after a refused change.</summary>
    internal void Refresh() => OnPropertyChanged(nameof(IsChosen));

    internal void Show(DisplayMonitor? monitor, string label, bool isChosen, bool enabled, string? toolTip)
    {
        if (!Equals(_monitor, monitor))
        {
            _monitor = monitor;
            OnPropertyChanged(nameof(Monitor));
            OnPropertyChanged(nameof(IsConnected));
        }

        Label = label;
        Set(ref _isChosen, isChosen, nameof(IsChosen));
        IsEnabled = enabled;
        ToolTip = toolTip;
    }
}
