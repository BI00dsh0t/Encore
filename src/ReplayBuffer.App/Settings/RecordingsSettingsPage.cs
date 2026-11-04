using System.Windows;
using System.Windows.Input;
using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Settings;

/// <summary>A monitor the Recordings page offers; a null device name is whichever monitor is primary.</summary>
internal sealed record MonitorItem(string? DeviceName, string Label);

/// <summary>
/// The Recordings page (story 6.11): the monitor a Monitor Recording captures, from the engine's
/// <c>monitors.list</c>. A choice is one Global <c>monitorRecordingMonitor</c> op. Members run on the UI thread.
/// </summary>
internal sealed class RecordingsSettingsPage : SettingsPage
{
    private const string PrimaryLabel = "Primary monitor";

    private readonly IEngineSession _engine;
    private string? _monitor = Keys.MonitorRecordingMonitor.Default;
    private IReadOnlyList<DisplayMonitor> _monitors = [];
    private IReadOnlyList<MonitorItem> _items = [new(null, PrimaryLabel)];
    private string? _fetchedInstance;

    public RecordingsSettingsPage(SettingsSender sender, IEngineSession engine)
        : base(sender)
    {
        _engine = engine;
        RefreshCommand = new RelayCommand(() => _ = FetchAsync());
    }

    public override string Title => "Recordings";

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
            _ = SendAsync([SettingsValues.SetGlobal(Keys.MonitorRecordingMonitor, value.DeviceName)]);
        }
    }

    /// <summary>Lists the monitors again.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>Fetches the monitors from a newly connected engine run. Called before <see cref="SettingsPage.Render"/>.</summary>
    public void ShowState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (EngineSessions.ConnectedState(_engine.Status, state) is { } connected && connected.EngineInstanceId != _fetchedInstance)
        {
            _fetchedInstance = connected.EngineInstanceId;
            _ = FetchAsync();
        }
    }

    protected override FrameworkElement CreateView() => new RecordingsSettingsView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
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
            Message = null;
            ShowMonitors();
        }
        catch (ErrorCodeException e)
        {
            Message = $"Couldn't list monitors ({e.Code}): {e.Message}";
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
