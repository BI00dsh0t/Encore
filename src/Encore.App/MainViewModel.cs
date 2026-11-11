using System.ComponentModel;
using System.Windows;
using Encore.App.Engine;
using Encore.App.Library;
using Encore.App.Settings;
using Encore.App.Status;
using Encore.App.Updates;
using Encore.Contracts.Settings;

namespace Encore.App;

/// <summary>The Main App window's state: the status line, the Library and the Settings window, all from the engine's state.</summary>
internal sealed class MainViewModel : Observable
{
    public const string LibraryTitle = "Library";

    private readonly IEngineSession _engine;
    private bool _isWindowShown;
    private bool _isNavigationExpanded = true;
    private PlayerViewModel? _watchedPlayer;

    /// <summary>Post runs an action on the UI thread, as session changes arrive on background threads.</summary>
    public MainViewModel(
        IEngineSession engine,
        Action<Action> post,
        string? libraryViewFile = null,
        string? defaultCacheFolder = null,
        IGameShell? gameShell = null,
        Func<IPlayer>? player = null,
        Action<string>? log = null,
        IClipMediaService? clipMedia = null,
        string? clipThumbnailCacheFolder = null,
        AppUpdates? updates = null,
        string? firstUseFile = null)
    {
        _engine = engine;
        StatusLine = new StatusLine(engine);
        SettingsViewModels = SettingsViewModelFactory.Create(new SettingsSender(engine), engine, defaultCacheFolder, gameShell, updates);
        General = SettingsViewModels.OfType<GeneralSettingsViewModel>().Single();
        Rewind = SettingsViewModels.OfType<RewindSettingsViewModel>().Single();
        Hotkeys = SettingsViewModels.OfType<HotkeysViewModel>().Single();
        TrackLayout = SettingsViewModels.OfType<TrackLayoutViewModel>().Single();
        GameProfiles = SettingsViewModels.OfType<GameProfilesViewModel>().Single();
        FeedbackSounds = SettingsViewModels.OfType<FeedbackSoundsViewModel>().Single();
        LibrarySettings = SettingsViewModels.OfType<LibrarySettingsViewModel>().Single();
        Settings = new SettingsWindowViewModel(SettingsViewModels);
        Library = new LibraryViewModel(
            engine,
            LibraryThumbnails.Shell(post),
            libraryViewFile,
            player: player,
            log: log,
            clipMedia: clipMedia,
            clipThumbnailCacheFolder: clipThumbnailCacheFolder,
            firstUseFile: firstUseFile);
        LibraryNav = new NavItem(LibraryTitle, null);
        NavItems = [LibraryNav];
        Library.DrillIn.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DrillIn.IsOpen))
            {
                OnPropertyChanged(nameof(IsDrilledIn));
            }
        };
        Library.Playback.PropertyChanged += OnPlaybackPropertyChanged;

        // The tips' Change hotkeys link opens the Settings window at Hotkeys.
        Library.Tips.ChangeHotkeys = () => OpenSettings(Hotkeys);

        // Edit the main setting, from a game's page: the page that holds the key.
        GameProfiles.EditMain = key => Settings.Select(key == Keys.TrackLayout ? TrackLayout : Rewind);
        engine.Changed += () => post(Refresh);
        Refresh();
    }

    /// <summary>The status line above every page: capture state and the active warnings.</summary>
    public StatusLine StatusLine { get; }

    public IReadOnlyList<SettingsViewModel> SettingsViewModels { get; }

    /// <summary>The General page, which the tray's Start with Windows item shares.</summary>
    public GeneralSettingsViewModel General { get; }

    /// <summary>Rewind page, which takes the snapshot's video options and storage estimate as well as its settings.</summary>
    public RewindSettingsViewModel Rewind { get; }

    /// <summary>The Hotkeys page, which takes the snapshot's <c>hotkey.status</c> as well as its settings.</summary>
    public HotkeysViewModel Hotkeys { get; }

    /// <summary>Audio Tracks, which takes the snapshot's device list and meters as well as its settings.</summary>
    public TrackLayoutViewModel TrackLayout { get; }

    /// <summary>Games: the list, and a page per game.</summary>
    public GameProfilesViewModel GameProfiles { get; }

    /// <summary>The Feedback Sounds page, which takes the snapshot's device list and warnings as well as its settings.</summary>
    public FeedbackSoundsViewModel FeedbackSounds { get; }

    /// <summary>The Library and Cleanup page, which takes the snapshot's Library folder, items and Cleanup block too.</summary>
    public LibrarySettingsViewModel LibrarySettings { get; }

    public LibraryViewModel Library { get; }

    /// <summary>The Settings window: its sections, the selected page and whether it is shown.</summary>
    public SettingsWindowViewModel Settings { get; }

    /// <summary>The main window's navigation: the Library; Settings opens in its own window.</summary>
    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>Whether the nav pane shows its labels, or only its icons.</summary>
    public bool IsNavigationExpanded
    {
        get => _isNavigationExpanded;
        set
        {
            if (Set(ref _isNavigationExpanded, value))
            {
                OnPropertyChanged(nameof(NavigationToggleLabel));
            }
        }
    }

    public string NavigationToggleLabel => IsNavigationExpanded ? "Collapse navigation" : "Expand navigation";

    public NavItem LibraryNav { get; }

    /// <summary>The Clips folder the engine uses, or null before it says: the tray's Open the Clips folder.</summary>
    public string? ClipsFolder => _engine.State?.LibraryFolder;

    /// <summary>The Library's drill-in; the shell's breadcrumb binds to it.</summary>
    public DrillIn CurrentDrillIn => Library.DrillIn;

    /// <summary>Whether an item page is open, so the page header is a breadcrumb.</summary>
    public bool IsCreatingClip => Library.Playback.Creator is not null;

    public bool IsDrilledIn => CurrentDrillIn.IsOpen;

    /// <summary>The Library chosen again in the nav pane: an open item page goes back to the list.</summary>
    public void ReselectLibrary() => Library.DrillIn.Close();

    /// <summary>Esc: leaves the player's full screen, else closes an open item page. False when it did neither.</summary>
    public bool HandleEscape()
    {
        if (IsPlayerFullScreen)
        {
            LeavePlayerFullScreen();
            return true;
        }

        return GoBack();
    }

    /// <summary>Alt+Left and the mouse's Back: closes an open item page. False on the Library's list.</summary>
    public bool GoBack()
    {
        if (!Library.DrillIn.IsOpen)
        {
            return false;
        }

        Library.DrillIn.Close();
        return true;
    }

    /// <summary>The Settings button and every link to a settings page: shows the Settings window, at the page when one is given.</summary>
    public void OpenSettings(SettingsViewModel? page = null) => Settings.Open(page);

    /// <summary>Whether the player page fills the screen: the window drops its frame, nav and headers.</summary>
    public bool IsPlayerFullScreen => Library.Playback.Player?.IsFullScreen == true;

    /// <summary>Esc in full screen: back to the window, the player page still open.</summary>
    public void LeavePlayerFullScreen()
    {
        if (Library.Playback.Player is { } player)
        {
            player.IsFullScreen = false;
        }
    }

    /// <summary>The page on screen: the Library's view.</summary>
    public FrameworkElement CurrentView => Library.View;

    /// <summary>Whether the Main App window is shown; closing it only hides it.</summary>
    public bool IsWindowShown
    {
        get => _isWindowShown;
        set
        {
            if (Set(ref _isWindowShown, value))
            {
                if (!value)
                {
                    Library.Playback.Pause();
                }
                else
                {
                    Library.Playback.ResumeCreator();
                }
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
        Rewind.ShowState(state);
        GameProfiles.ShowState(state);
        Hotkeys.ShowStatus(state.Hotkeys);
        FeedbackSounds.ShowState(state);
        Hotkeys.ShowMonitors(state);
        LibrarySettings.ShowState(state);
        Library.Render(state);

        // Before rendering, so a draft the render watches is recorded against this engine run.
        TrackLayout.ShowMeters(state.Meters, state.EngineInstanceId);
        foreach (var viewModel in SettingsViewModels)
        {
            viewModel.Render(state.Settings);
        }
    }

    // The shell follows the player page's full screen; a page closing leaves it.
    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryPlayback.Creator))
        {
            OnPropertyChanged(nameof(IsCreatingClip));
        }

        if (e.PropertyName == nameof(LibraryPlayback.Player))
        {
            WatchPlayer(Library.Playback.Player);
        }
    }

    private void WatchPlayer(PlayerViewModel? player)
    {
        if (_watchedPlayer is { } old)
        {
            old.PropertyChanged -= OnPlayerPropertyChanged;
        }

        _watchedPlayer = player;
        if (player is not null)
        {
            player.PropertyChanged += OnPlayerPropertyChanged;
        }

        OnPropertyChanged(nameof(IsPlayerFullScreen));
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsFullScreen))
        {
            OnPropertyChanged(nameof(IsPlayerFullScreen));
        }
    }
}
