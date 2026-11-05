using System.Windows.Input;
using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Settings;

/// <summary>A screen the Monitor Recording picker offers; a null device name is whichever monitor is primary.</summary>
internal sealed record MonitorItem(string? DeviceName, string Label);

/// <summary>
/// The screen a Monitor Recording captures (story 6.11's Recordings page, moved beside the Monitor
/// Recording key on Hotkeys by story 10.9), from the engine's <c>monitors.list</c>. A choice is one
/// Global <c>monitorRecordingMonitor</c> op, sent through the hosting page. Its own component, so story
/// 11.4's choice of several screens can replace it in one place. Members run on the UI thread.
/// </summary>
internal sealed class MonitorPicker : Observable
{
    private const string PrimaryLabel = "Primary monitor";

    private readonly IEngineSession _engine;
    private readonly Func<IReadOnlyList<SettingsOp>, Task> _send;
    private readonly Action<string?> _message;
    private string? _monitor = Keys.MonitorRecordingMonitor.Default;
    private IReadOnlyList<DisplayMonitor> _monitors = [];
    private IReadOnlyList<MonitorItem> _items = [new(null, PrimaryLabel)];
    private string? _fetchedInstance;
    private MonitorList? _changed;

    /// <param name="send">Sends the ops, as the hosting page's <c>SendAsync</c>.</param>
    /// <param name="message">Shows a note on the hosting page's error bar; null clears it.</param>
    public MonitorPicker(IEngineSession engine, Func<IReadOnlyList<SettingsOp>, Task> send, Action<string?> message)
    {
        _engine = engine;
        _send = send;
        _message = message;
        RefreshCommand = new RelayCommand(() => _ = FetchAsync());
    }

    /// <summary>Primary monitor first, then the attached monitors, then the set monitor if it isn't attached.</summary>
    public IReadOnlyList<MonitorItem> Monitors
    {
        get => _items;
        private set => Set(ref _items, value);
    }

    public MonitorItem? SelectedMonitor
    {
        get => _items.FirstOrDefault(m => SameDevice(m.DeviceName, _monitor));
        set
        {
            // A combo box whose list changed clears its selection; that isn't a choice.
            if (value is null || SameDevice(value.DeviceName, _monitor))
            {
                return;
            }

            _monitor = value.DeviceName;
            OnPropertyChanged();
            _ = _send([SettingsValues.SetGlobal(Keys.MonitorRecordingMonitor, value.DeviceName)]);
        }
    }

    /// <summary>Lists the monitors again.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>
    /// Fetches the monitors from a newly connected engine run, and shows the list a <c>monitors.changed</c>
    /// carries (T-03). Called before the hosting page renders.
    /// </summary>
    public void ShowState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (EngineSessions.ConnectedState(_engine.Status, state) is not { } connected)
        {
            return;
        }

        if (connected.EngineInstanceId != _fetchedInstance)
        {
            _fetchedInstance = connected.EngineInstanceId;
            _changed = connected.Monitors;
            _ = FetchAsync();
        }
        else if (connected.Monitors is { } changed && !ReferenceEquals(changed, _changed))
        {
            _changed = changed;
            _monitors = changed.Monitors;
            ShowMonitors();
        }
    }

    /// <summary>Shows the document's <c>monitorRecordingMonitor</c>.</summary>
    public void Render(SettingsDocument document)
    {
        _monitor = SettingsValues.Global(document, Keys.MonitorRecordingMonitor);
        ShowMonitors();
    }

    private static bool SameDevice(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>"DISPLAY2" for <c>\\.\DISPLAY2</c>.</summary>
    private static string ShortName(string deviceName) => deviceName.StartsWith(@"\\.\", StringComparison.Ordinal) ? deviceName[4..] : deviceName;

    private async Task FetchAsync()
    {
        try
        {
            _monitors = (await _engine.SendAsync(RecordingMessages.MonitorsList, Empty.Value)).Monitors;
            _message(null);
            ShowMonitors();
        }
        catch (ErrorCodeException e)
        {
            _message($"Couldn't list screens ({e.Code}): {e.Message}");
        }
    }

    private void ShowMonitors()
    {
        bool Shared(string name) => _monitors.Count(m => string.Equals(m.FriendlyName, name, StringComparison.OrdinalIgnoreCase)) > 1;

        List<MonitorItem> items = [new(null, PrimaryLabel)];
        items.AddRange(_monitors.Select(m => new MonitorItem(
            m.DeviceName,
            $"{m.FriendlyName} — {m.Width}×{m.Height}"
                + (m.Primary ? " (primary)" : "")
                + (Shared(m.FriendlyName) ? $" ({ShortName(m.DeviceName)})" : ""))));
        if (_monitor is { } device && !items.Any(i => SameDevice(i.DeviceName, device)))
        {
            items.Add(new(device, device + " (not connected)"));
        }

        // The selection follows the list, so a combo box that cleared its selection gets it back.
        if (!items.SequenceEqual(_items))
        {
            Monitors = items;
        }

        OnPropertyChanged(nameof(SelectedMonitor));
    }
}
