using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Library;
using ReplayBuffer.App.Settings;
using ReplayBuffer.App.Status;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.App;

/// <summary>
/// The Main App window's state: the status line, the Library and the Settings pages. Everything
/// shown comes from <see cref="IEngineSession.State"/> (AD-3).
/// </summary>
internal sealed class MainViewModel : Observable
{
    private readonly IEngineSession _engine;
    private SettingsPage _selectedPage;
    private bool _isWindowShown;
    private bool _isSettingsTabSelected;

    /// <param name="post">Runs an action on the UI thread; session changes arrive on background threads.</param>
    /// <param name="libraryViewFile">Where the Library remembers its view choices; without it, they last only this run.</param>
    public MainViewModel(IEngineSession engine, Action<Action> post, string? libraryViewFile = null)
    {
        _engine = engine;
        StatusLine = new StatusLine(engine);
        Pages = SettingsPages.Create(new SettingsSender(engine), engine);
        General = Pages.OfType<GeneralSettingsPage>().Single();
        ReplayBuffer = Pages.OfType<ReplayBufferSettingsPage>().Single();
        Hotkeys = Pages.OfType<HotkeysPage>().Single();
        TrackLayout = Pages.OfType<TrackLayoutPage>().Single();
        GameProfiles = Pages.OfType<GameProfilesPage>().Single();
        _selectedPage = Pages[0];
        Library = new LibraryPage(engine, LibraryThumbnails.Shell(post), libraryViewFile);
        engine.Changed += () => post(Refresh);
        Refresh();
    }

    /// <summary>The status line above every page: capture state and the active warnings.</summary>
    public StatusLine StatusLine { get; }

    public IReadOnlyList<SettingsPage> Pages { get; }

    /// <summary>The General page, which the tray's Start with Windows item shares.</summary>
    public GeneralSettingsPage General { get; }

    /// <summary>The Replay Buffer page, which takes the snapshot's video options and storage estimate as well as its settings.</summary>
    public ReplayBufferSettingsPage ReplayBuffer { get; }

    /// <summary>The Hotkeys page, which takes the snapshot's <c>hotkey.status</c> as well as its settings.</summary>
    public HotkeysPage Hotkeys { get; }

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

    /// <summary>Reads the session again. Runs on the UI thread.</summary>
    public void Refresh()
    {
        StatusLine.Render(_engine.Status, _engine.Problem, _engine.State);

        if (_engine.State is not { } state)
        {
            return;
        }

        TrackLayout.ShowDevices(state.Devices);
        ReplayBuffer.ShowState(state);
        Hotkeys.ShowStatus(state.Hotkeys);
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
}
