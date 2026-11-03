using System.Globalization;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Library;
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
    private string _targetGame = "";
    private string _settingsRevision = "";
    private IReadOnlyList<string> _warnings = [];
    private SettingsPage _selectedPage;
    private bool _isWindowShown;
    private bool _isSettingsTabSelected;

    /// <param name="post">Runs an action on the UI thread; session changes arrive on background threads.</param>
    public MainViewModel(IEngineSession engine, Action<Action> post)
    {
        _engine = engine;
        Pages = SettingsPages.Create(new SettingsSender(engine), engine);
        General = Pages.OfType<GeneralSettingsPage>().Single();
        TrackLayout = Pages.OfType<TrackLayoutPage>().Single();
        GameProfiles = Pages.OfType<GameProfilesPage>().Single();
        _selectedPage = Pages[0];
        Library = new LibraryPage();
        engine.Changed += () => post(Refresh);
        Refresh();
    }

    public IReadOnlyList<SettingsPage> Pages { get; }

    /// <summary>The General page, which the tray's Start with Windows item shares.</summary>
    public GeneralSettingsPage General { get; }

    /// <summary>The Track Layout page, which takes the snapshot's device list as well as its settings.</summary>
    public TrackLayoutPage TrackLayout { get; }

    /// <summary>The Game Profiles page.</summary>
    public GameProfilesPage GameProfiles { get; }

    /// <summary>The Library tab.</summary>
    public LibraryPage Library { get; }

    public SettingsPage SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (Set(ref _selectedPage, value))
            {
                UpdateOpenPage();
            }
        }
    }

    /// <summary>Whether the Main App window is shown; closing it only hides it (AD-16).</summary>
    public bool IsWindowShown
    {
        get => _isWindowShown;
        set
        {
            if (Set(ref _isWindowShown, value))
            {
                UpdateOpenPage();
            }
        }
    }

    /// <summary>Whether the window's Settings tab is selected.</summary>
    public bool IsSettingsTabSelected
    {
        get => _isSettingsTabSelected;
        set
        {
            if (Set(ref _isSettingsTabSelected, value))
            {
                UpdateOpenPage();
            }
        }
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

    /// <summary>The Target Game line: its name and process, or none.</summary>
    public string TargetGame
    {
        get => _targetGame;
        private set => Set(ref _targetGame, value);
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
            EngineStatus.Restarting => "Restarting the engine…" + (_engine.Problem is { } why ? " " + why : ""),
            EngineStatus.NotRunning => "The engine isn't running." + (_engine.Problem is { } problem ? " " + problem : ""),
            _ => "The engine disconnected. Reconnecting…",
        };

        if (_engine.State is not { } state)
        {
            return;
        }

        EngineDetails = string.Create(CultureInfo.InvariantCulture, $"Process {state.Pid}, run {state.EngineInstanceId}, event {state.Seq}");
        var capture = state.Capture;
        Capture = $"Replay Buffer {Name(capture.Replay)}, Recording {Name(capture.Recording)}, " +
            $"Monitor Recording {Name(capture.MonitorRecording)}, mic {(capture.MicMuted ? "muted" : "live")}";
        TargetGame = state.TargetGame is { } target
            ? string.Create(CultureInfo.InvariantCulture, $"Target Game: {target.Name} (process {target.ProcessId})")
            : "Target Game: none";
        SettingsRevision = state.Settings.Revision.ToString(CultureInfo.InvariantCulture);
        Warnings = [.. state.Warnings.Select(w => $"{w.Severity}: {w.Message} ({w.Code})")];
        TrackLayout.ShowDevices(state.Devices);
        Library.Render(state);

        // Before rendering, so a draft the render watches is recorded against this engine run.
        TrackLayout.ShowMeters(state.Meters, state.EngineInstanceId);
        foreach (var page in Pages)
        {
            page.Render(state.Settings);
        }
    }

    // The Track Layout page meters only while on screen, so a hidden window doesn't keep the engine metering.
    private void UpdateOpenPage() =>
        TrackLayout.IsOpen = IsWindowShown && IsSettingsTabSelected && SelectedPage == TrackLayout;

    private static string Name<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}
