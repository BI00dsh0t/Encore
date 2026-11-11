using System.ComponentModel;
using System.Windows;
using Encore.App.Engine;
using Encore.App.Library;
using Encore.App.Settings;
using Encore.App.Status;
using Encore.App.Updates;
using Encore.Contracts.Settings;

namespace Encore.App;

/// <summary>The Main App window's state: the status line, the Library and the Settings pages, all from the engine's state.</summary>
internal sealed class MainViewModel : Observable
{
    public const string LibraryTitle = "Library";
    public const string SettingsLabel = "Settings";

    private readonly IEngineSession _engine;
    private SettingsViewModel _selectedSettingsViewModel;
    private bool _isWindowShown;
    private bool _isNavigationExpanded = true;
    private bool _isSettingsTabSelected;
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
        _selectedSettingsViewModel = SettingsViewModels[0];
        Library = new LibraryViewModel(
            engine,
            LibraryThumbnails.Shell(post),
            libraryViewFile,
            player: player,
            log: log,
            clipMedia: clipMedia,
            clipThumbnailCacheFolder: clipThumbnailCacheFolder,
            firstUseFile: firstUseFile);
        LibraryNav = new NavItem(LibraryTitle, null, isSection: false);
        NavItems =
        [
            LibraryNav,
            new NavItem(SettingsLabel, null, isSection: true),
            .. SettingsViewModels.Select(viewModel => new NavItem(viewModel.Title, viewModel, isSection: false)),
        ];
        FollowDrillIns();
        Library.Playback.PropertyChanged += OnPlaybackPropertyChanged;

        // The tips' Change hotkeys link.
        Library.Tips.ChangeHotkeys = () => Select(Hotkeys);

        // Edit the main setting, from a game's page: the page that holds the key.
        GameProfiles.EditMain = key => Select(key == Keys.TrackLayout ? TrackLayout : Rewind);
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

    /// <summary>The shell's navigation: Library, the Settings label, then every settings page; only the label isn't selectable.</summary>
    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>The items of the nav list: today, the Library.</summary>
    public IEnumerable<NavItem> PrimaryNavItems => NavItems.Where(item => !item.IsSection && item.ViewModel is null);

    /// <summary>The items of the settings section list: one per settings page.</summary>
    public IEnumerable<NavItem> SettingsNavItems => NavItems.Where(item => item.ViewModel is not null);

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

    /// <summary>The selected navigation item; setting it sets the Library or Settings choice and the selected page.</summary>
    public NavItem SelectedNavItem
    {
        get => IsSettingsTabSelected ? NavItems.First(item => item.ViewModel == SelectedSettingsViewModel) : LibraryNav;
        set
        {
            if (value is null || value.IsSection)
            {
                // Never empty, and the section label can't be chosen: keep the item shown.
                OnPropertyChanged();
                return;
            }

            if (value.ViewModel is { } viewModel)
            {
                SelectedSettingsViewModel = viewModel;
                IsSettingsTabSelected = true;
            }
            else
            {
                IsSettingsTabSelected = false;
            }
        }
    }

    /// <summary>The nav list's selection: the Library, or none in Settings, so choosing the Library always leaves Settings.</summary>
    public NavItem? SelectedPrimaryNavItem
    {
        get => IsSettingsTabSelected ? null : LibraryNav;
        set
        {
            // The list clears its selection when Settings opens; only a chosen item navigates.
            if (value is not null)
            {
                SelectedNavItem = value;
            }
        }
    }

    /// <summary>The Clips folder the engine uses, or null before it says: the tray's Open the Clips folder.</summary>
    public string? ClipsFolder => _engine.State?.LibraryFolder;

    /// <summary>The shown page's drill-in, or null; the shell's breadcrumb binds to it.</summary>
    public DrillIn? CurrentDrillIn => IsSettingsTabSelected ? SelectedSettingsViewModel.DrillIn : Library.DrillIn;

    /// <summary>Whether an item page is open, so the page header is a breadcrumb.</summary>
    public bool IsCreatingClip => !IsSettingsTabSelected && Library.Playback.Creator is not null;

    public bool IsDrilledIn => CurrentDrillIn?.IsOpen == true;

    /// <summary>The nav item chosen again on the page already shown: an open drill-in goes back to its list.</summary>
    public void ReselectNavItem(NavItem item)
    {
        if (ReferenceEquals(item, SelectedNavItem))
        {
            CurrentDrillIn?.Close();
        }
    }

    /// <summary>Esc: leaves the player's full screen, else goes back as <see cref="GoBack"/> does. False when it did neither.</summary>
    public bool HandleEscape()
    {
        if (IsPlayerFullScreen)
        {
            LeavePlayerFullScreen();
            return true;
        }

        return GoBack();
    }

    /// <summary>Alt+Left and the mouse's Back: closes an open item page, else leaves Settings. False on the Library's list.</summary>
    public bool GoBack()
    {
        if (CurrentDrillIn is { IsOpen: true } drillIn)
        {
            drillIn.Close();
            return true;
        }

        if (!IsSettingsTabSelected)
        {
            return false;
        }

        SelectedNavItem = LibraryNav;
        return true;
    }

    /// <summary>The Settings button: shows the settings pages; pressed again on them, an open item page goes back to its list.</summary>
    public void OpenSettings()
    {
        if (IsSettingsTabSelected)
        {
            ReselectNavItem(SelectedNavItem);
        }

        IsSettingsTabSelected = true;
    }

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

    /// <summary>The page header's title: "Library" or the settings page's.</summary>
    public string CurrentTitle => SelectedNavItem.Title;

    /// <summary>The page on screen: the Library's view or the settings page's.</summary>
    public FrameworkElement CurrentView => IsSettingsTabSelected ? SelectedSettingsViewModel.View : Library.View;

    public SettingsViewModel SelectedSettingsViewModel
    {
        get => _selectedSettingsViewModel;
        set
        {
            if (Set(ref _selectedSettingsViewModel, value))
            {
                NavigationChanged();
                UpdateOpenPage();
            }
        }
    }

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

                UpdateOpenPage();
            }
        }
    }

    /// <summary>Whether a settings page is selected, rather than the Library; the tray's open-Settings path sets it.</summary>
    public bool IsSettingsTabSelected
    {
        get => _isSettingsTabSelected;
        set
        {
            if (Set(ref _isSettingsTabSelected, value))
            {
                NavigationChanged();
                UpdateOpenPage();
            }
        }
    }

    /// <summary>Selects the next or previous selectable navigation item, wrapping round: Ctrl+Tab and Ctrl+Shift+Tab.</summary>
    public void NavigateBy(int step)
    {
        var items = NavItems.Where(item => !item.IsSection).ToList();
        var at = items.IndexOf(SelectedNavItem);
        SelectedNavItem = items[(((at + step) % items.Count) + items.Count) % items.Count];
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

    private void Select(SettingsViewModel viewModel) => SelectedNavItem = NavItems.First(item => item.ViewModel == viewModel);

    // The page header is a breadcrumb while any page's item page is open.
    private void FollowDrillIns()
    {
        foreach (var drillIn in SettingsViewModels.Select(viewModel => viewModel.DrillIn).OfType<DrillIn>().Append(Library.DrillIn))
        {
            drillIn.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DrillIn.IsOpen))
                {
                    OnPropertyChanged(nameof(IsDrilledIn));
                }
            };
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

    private void NavigationChanged()
    {
        // Leaving a page closes its drill-in; leaving the Library closes its player page and releases the file.
        if (IsSettingsTabSelected)
        {
            Library.DrillIn.Close();
        }

        foreach (var viewModel in SettingsViewModels)
        {
            if (!IsSettingsTabSelected || !ReferenceEquals(viewModel, SelectedSettingsViewModel))
            {
                viewModel.DrillIn?.Close();
            }
        }

        OnPropertyChanged(nameof(IsCreatingClip));
        OnPropertyChanged(nameof(CurrentDrillIn));
        OnPropertyChanged(nameof(IsDrilledIn));
        OnPropertyChanged(nameof(SelectedNavItem));
        OnPropertyChanged(nameof(SelectedPrimaryNavItem));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentView));
    }

    // Pages meter only while on screen; the closing page goes first, so its unwatch doesn't stop the new watch.
    private void UpdateOpenPage()
    {
        var settingsShown = IsWindowShown && IsSettingsTabSelected;
        var trackLayout = settingsShown && SelectedSettingsViewModel == TrackLayout;
        var gameProfiles = settingsShown && SelectedSettingsViewModel == GameProfiles;
        if (!trackLayout)
        {
            TrackLayout.IsOpen = false;
        }

        if (!gameProfiles)
        {
            GameProfiles.IsOpen = false;
        }

        TrackLayout.IsOpen = trackLayout;
        GameProfiles.IsOpen = gameProfiles;
    }
}
