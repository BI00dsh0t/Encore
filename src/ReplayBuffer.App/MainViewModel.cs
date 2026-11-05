using System.Windows;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Library;
using ReplayBuffer.App.Settings;
using ReplayBuffer.App.Status;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App;

/// <summary>
/// The Main App window's state: the status line, the Library and the Settings pages. Everything
/// shown comes from <see cref="IEngineSession.State"/> (AD-3).
/// </summary>
internal sealed class MainViewModel : Observable
{
    public const string LibraryTitle = "Library";
    public const string SettingsLabel = "Settings";

    private readonly IEngineSession _engine;
    private SettingsPage _selectedPage;
    private bool _isWindowShown;
    private bool _isSettingsTabSelected;

    /// <param name="post">Runs an action on the UI thread; session changes arrive on background threads.</param>
    /// <param name="libraryViewFile">Where the Library remembers its view choices; without it, they last only this run.</param>
    /// <param name="defaultCacheFolder">The engine's default cache folder, shown on the Clips page.</param>
    public MainViewModel(IEngineSession engine, Action<Action> post, string? libraryViewFile = null, string? defaultCacheFolder = null, IGameShell? gameShell = null)
    {
        _engine = engine;
        StatusLine = new StatusLine(engine);
        Pages = SettingsPages.Create(new SettingsSender(engine), engine, defaultCacheFolder, gameShell);
        General = Pages.OfType<GeneralSettingsPage>().Single();
        ReplayBuffer = Pages.OfType<ReplayBufferSettingsPage>().Single();
        Hotkeys = Pages.OfType<HotkeysPage>().Single();
        TrackLayout = Pages.OfType<TrackLayoutPage>().Single();
        GameProfiles = Pages.OfType<GameProfilesPage>().Single();
        FeedbackSounds = Pages.OfType<FeedbackSoundsPage>().Single();
        LibrarySettings = Pages.OfType<LibrarySettingsPage>().Single();
        _selectedPage = Pages[0];
        Library = new LibraryPage(engine, LibraryThumbnails.Shell(post), libraryViewFile);
        LibraryNav = new NavItem(LibraryTitle, null, isSection: false);
        NavItems = [LibraryNav, new NavItem(SettingsLabel, null, isSection: true), .. Pages.Select(p => new NavItem(p.Title, p, isSection: false))];
        foreach (var drillIn in Pages.Select(p => p.DrillIn).OfType<DrillIn>())
        {
            drillIn.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DrillIn.IsOpen))
                {
                    OnPropertyChanged(nameof(IsDrilledIn));
                }
            };
        }

        // The welcome card's links (story 10.6).
        Library.Welcome.Navigate = link => SelectedNavItem = NavItems.First(i => i.Page == link switch
        {
            WelcomeLink.AudioTracks => (SettingsPage)TrackLayout,
            WelcomeLink.Storage => LibrarySettings,
            _ => GameProfiles,
        });

        // Edit the main setting, from a game's page: the page that holds the key.
        GameProfiles.EditMain = key => SelectedNavItem = NavItems.First(i => i.Page == (key == Keys.TrackLayout ? TrackLayout : (SettingsPage)ReplayBuffer));
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

    /// <summary>The Feedback Sounds page, which takes the snapshot's device list and warnings as well as its settings.</summary>
    public FeedbackSoundsPage FeedbackSounds { get; }

    /// <summary>The Library and Cleanup page, which takes the snapshot's Library folder, items and Cleanup block as well as its settings.</summary>
    public LibrarySettingsPage LibrarySettings { get; }

    /// <summary>The Library page.</summary>
    public LibraryPage Library { get; }

    /// <summary>
    /// The shell's navigation (EXPERIENCE.md): Library, the Settings section label, then every settings
    /// page in <see cref="Pages"/>' order. Only the label isn't selectable.
    /// </summary>
    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>The navigation's Library item.</summary>
    public NavItem LibraryNav { get; }

    /// <summary>
    /// The selected navigation item: Library unless <see cref="IsSettingsTabSelected"/>, else the item of
    /// <see cref="SelectedPage"/>. Setting it sets those two, so every path that sets them selects its item.
    /// </summary>
    public NavItem SelectedNavItem
    {
        get => IsSettingsTabSelected ? NavItems.First(i => i.Page == SelectedPage) : LibraryNav;
        set
        {
            if (value is null || value.IsSection)
            {
                // Never empty, and the section label can't be chosen: keep the item shown.
                OnPropertyChanged();
                return;
            }

            if (value.Page is { } page)
            {
                SelectedPage = page;
                IsSettingsTabSelected = true;
            }
            else
            {
                IsSettingsTabSelected = false;
            }
        }
    }

    /// <summary>The Clips folder the engine uses, or null before it says: the tray's Open the Clips folder.</summary>
    public string? ClipsFolder => _engine.State?.LibraryFolder;

    /// <summary>The shown page's drill-in (story 10.10), or null: the shell's breadcrumb binds to it.</summary>
    public DrillIn? CurrentDrillIn => IsSettingsTabSelected ? SelectedPage.DrillIn : null;

    /// <summary>Whether an item page is open, so the page header is a breadcrumb.</summary>
    public bool IsDrilledIn => CurrentDrillIn?.IsOpen == true;

    /// <summary>
    /// The nav item chosen again, by click, Enter or Space: on the page already shown, an open drill-in
    /// item page goes back to its list (story 10.10).
    /// </summary>
    public void ReselectNavItem(NavItem item)
    {
        if (ReferenceEquals(item, SelectedNavItem))
        {
            CurrentDrillIn?.Close();
        }
    }

    /// <summary>The page header's title: "Library" or the settings page's.</summary>
    public string CurrentTitle => SelectedNavItem.Title;

    /// <summary>The page on screen: the Library's view or the settings page's.</summary>
    public FrameworkElement CurrentView => IsSettingsTabSelected ? SelectedPage.View : Library.View;

    public SettingsPage SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (Set(ref _selectedPage, value))
            {
                NavigationChanged();
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

    /// <summary>
    /// Selects the next (<paramref name="step"/> 1) or previous (-1) selectable navigation item, wrapping
    /// round: Ctrl+Tab and Ctrl+Shift+Tab.
    /// </summary>
    public void NavigateBy(int step)
    {
        var items = NavItems.Where(i => !i.IsSection).ToList();
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
        ReplayBuffer.ShowState(state);
        GameProfiles.ShowState(state);
        Hotkeys.ShowStatus(state.Hotkeys);
        FeedbackSounds.ShowState(state);
        Hotkeys.ShowMonitors(state);
        LibrarySettings.ShowState(state);
        Library.Render(state);

        // Before rendering, so a draft the render watches is recorded against this engine run.
        TrackLayout.ShowMeters(state.Meters, state.EngineInstanceId);
        foreach (var page in Pages)
        {
            page.Render(state.Settings);
        }
    }

    private void NavigationChanged()
    {
        // Leaving a page shows its list when it's shown again (story 10.10's drill-in).
        foreach (var page in Pages)
        {
            if (!IsSettingsTabSelected || !ReferenceEquals(page, SelectedPage))
            {
                page.DrillIn?.Close();
            }
        }

        OnPropertyChanged(nameof(CurrentDrillIn));
        OnPropertyChanged(nameof(IsDrilledIn));
        OnPropertyChanged(nameof(SelectedNavItem));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentView));
    }

    // The Track Layout pages meter only while on screen, so a hidden window doesn't keep the engine metering.
    // The closing page goes first, so its meters.unwatch doesn't stop the opening page's watch.
    private void UpdateOpenPage()
    {
        var settingsShown = IsWindowShown && IsSettingsTabSelected;
        var trackLayout = settingsShown && SelectedPage == TrackLayout;
        var gameProfiles = settingsShown && SelectedPage == GameProfiles;
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

/// <summary>One item of the shell's navigation: the Library, the Settings section label, or a settings page.</summary>
internal sealed class NavItem : Observable
{
    public NavItem(string title, SettingsPage? page, bool isSection)
    {
        Title = title;
        Page = page;
        IsSection = isSection;
        if (page is not null)
        {
            page.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsPage.HasUnsavedChanges))
                {
                    OnPropertyChanged(nameof(HasUnsavedChanges));
                }
            };
        }
    }

    /// <summary>The nav item's dot: its page holds unsaved changes.</summary>
    public bool HasUnsavedChanges => Page?.HasUnsavedChanges ?? false;

    /// <summary>The page's title, in sentence case; Gaming dark uppercases it in its template only.</summary>
    public string Title { get; }

    /// <summary>The settings page it opens; null for the Library and the section label.</summary>
    public SettingsPage? Page { get; }

    /// <summary>The non-selectable Settings label above the settings pages.</summary>
    public bool IsSection { get; }

    public override string ToString() => Title;
}
