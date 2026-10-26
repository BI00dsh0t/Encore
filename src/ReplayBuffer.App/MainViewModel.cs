using System.Globalization;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Settings;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.App;

/// <summary>
/// The Main App window's state: the engine's snapshot as text for the Status area, and the Settings
/// pages. Everything shown comes from <see cref="IEngineSession.State"/> (AD-3).
/// </summary>
internal sealed class MainViewModel : Observable
{
    private readonly IEngineSession _engine;
    private string _status = "";
    private string _engineDetails = "";
    private string _capture = "";
    private string _settingsRevision = "";
    private IReadOnlyList<string> _warnings = [];
    private SettingsPage _selectedPage;

    /// <param name="post">Runs an action on the UI thread; session changes arrive on background threads.</param>
    public MainViewModel(IEngineSession engine, Action<Action> post)
    {
        _engine = engine;
        Pages = SettingsPages.Create(new SettingsSender(engine));
        General = Pages.OfType<GeneralSettingsPage>().Single();
        _selectedPage = Pages[0];
        engine.Changed += () => post(Refresh);
        Refresh();
    }

    public IReadOnlyList<SettingsPage> Pages { get; }

    /// <summary>The General page, which the tray's Start with Windows item shares.</summary>
    public GeneralSettingsPage General { get; }

    public SettingsPage SelectedPage
    {
        get => _selectedPage;
        set => Set(ref _selectedPage, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string EngineDetails
    {
        get => _engineDetails;
        private set => Set(ref _engineDetails, value);
    }

    public string Capture
    {
        get => _capture;
        private set => Set(ref _capture, value);
    }

    public string SettingsRevision
    {
        get => _settingsRevision;
        private set => Set(ref _settingsRevision, value);
    }

    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        private set => Set(ref _warnings, value);
    }

    /// <summary>Reads the session again. Runs on the UI thread.</summary>
    public void Refresh()
    {
        Status = _engine.Status switch
        {
            EngineStatus.Connecting => "Connecting to the engine…",
            EngineStatus.Connected => "Connected to the engine.",
            EngineStatus.NotRunning => "The engine isn't running." + (_engine.Problem is { } problem ? " " + problem : ""),
            _ => "The engine disconnected. Quit and start ReplayBuffer again to reconnect.",
        };

        if (_engine.State is not { } state)
        {
            return;
        }

        EngineDetails = string.Create(CultureInfo.InvariantCulture, $"Process {state.Pid}, run {state.EngineInstanceId}, event {state.Seq}");
        var capture = state.Capture;
        Capture = $"Replay Buffer {Name(capture.Replay)}, Recording {Name(capture.Recording)}, " +
            $"Monitor Recording {Name(capture.MonitorRecording)}, mic {(capture.MicMuted ? "muted" : "live")}";
        SettingsRevision = state.Settings.Revision.ToString(CultureInfo.InvariantCulture);
        Warnings = [.. state.Warnings.Select(w => $"{w.Severity}: {w.Message} ({w.Code})")];
        foreach (var page in Pages)
        {
            page.Render(state.Settings);
        }
    }

    private static string Name<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}
