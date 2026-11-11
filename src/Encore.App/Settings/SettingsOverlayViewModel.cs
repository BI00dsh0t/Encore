using System.Windows;
using System.Windows.Input;

namespace Encore.App.Settings;

/// <summary>The Settings overlay's state: whether it is open, a section per page, and the selected page with its drill-in.</summary>
internal sealed class SettingsOverlayViewModel : Observable
{
    private NavItem _selectedSection;
    private bool _isOpen;
    private bool _isWindowShown;

    public SettingsOverlayViewModel(IReadOnlyList<SettingsViewModel> pages)
    {
        CloseCommand = new RelayCommand(Close);
        Sections = pages.Select(page => new NavItem(page.Title, page)).ToList();
        _selectedSection = Sections[0];
        foreach (var drillIn in pages.Select(page => page.DrillIn).OfType<DrillIn>())
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

    /// <summary>One section per settings page, in the pages' order.</summary>
    public IReadOnlyList<NavItem> Sections { get; }

    /// <summary>The highlighted section; choosing another closes the item page of the one left.</summary>
    public NavItem SelectedSection
    {
        get => _selectedSection;
        set
        {
            // The list never goes empty: a cleared selection keeps the page shown.
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }

            var left = _selectedSection;
            if (!Set(ref _selectedSection, value))
            {
                return;
            }

            left.ViewModel!.DrillIn?.Close();
            OnPropertyChanged(nameof(SelectedPage));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(CurrentView));
            OnPropertyChanged(nameof(CurrentDrillIn));
            OnPropertyChanged(nameof(IsDrilledIn));
            UpdateOpenPage();
        }
    }

    public SettingsViewModel SelectedPage => SelectedSection.ViewModel!;

    /// <summary>The page header's title.</summary>
    public string Title => SelectedPage.Title;

    public FrameworkElement CurrentView => SelectedPage.View;

    /// <summary>The selected page's drill-in, or null; the window's breadcrumb binds to it.</summary>
    public DrillIn? CurrentDrillIn => SelectedPage.DrillIn;

    /// <summary>Whether an item page is open, so the page header is a breadcrumb.</summary>
    public bool IsDrilledIn => CurrentDrillIn?.IsOpen == true;

    /// <summary>Whether the overlay is open over the Main App window, which then takes no input behind it.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (Set(ref _isOpen, value))
            {
                UpdateOpenPage();
            }
        }
    }

    /// <summary>Whether the Main App window is shown; hidden in the tray, the overlay stays open but its page stops watching.</summary>
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

    /// <summary>The panel's close button.</summary>
    public ICommand CloseCommand { get; }

    /// <summary>Opens the overlay at <paramref name="page"/>, or at the section last shown; already open, it switches section.</summary>
    public void Open(SettingsViewModel? page = null)
    {
        if (page is not null)
        {
            Select(page);
        }

        IsOpen = true;
    }

    /// <summary>The close button, a click on the scrim, or Esc with no item page open.</summary>
    public void Close() => IsOpen = false;

    /// <summary>Esc: goes back from an item page, else closes the overlay.</summary>
    public void Escape()
    {
        if (!GoBack())
        {
            Close();
        }
    }

    public void Select(SettingsViewModel page) => SelectedSection = Sections.First(section => section.ViewModel == page);

    /// <summary>The shown section chosen again: an open item page goes back to its list.</summary>
    public void ReselectSection(NavItem section)
    {
        if (ReferenceEquals(section, SelectedSection))
        {
            CurrentDrillIn?.Close();
        }
    }

    /// <summary>Esc, Alt+Left and the mouse's Back: closes an open item page. False on a page's list.</summary>
    public bool GoBack()
    {
        if (CurrentDrillIn is not { IsOpen: true } drillIn)
        {
            return false;
        }

        drillIn.Close();
        return true;
    }

    /// <summary>Selects the next or previous section, wrapping round: Ctrl+Tab and Ctrl+Shift+Tab.</summary>
    public void StepBy(int step)
    {
        var count = Sections.Count;
        SelectedSection = Sections[(Sections.ToList().IndexOf(SelectedSection) + step % count + count) % count];
    }

    // The closing page goes first, so its unwatch doesn't stop the new page's watch.
    private void UpdateOpenPage()
    {
        foreach (var page in Sections.Select(section => section.ViewModel!).Where(page => !IsOnScreen(page)))
        {
            page.IsOpen = false;
        }

        SelectedPage.IsOpen = IsOnScreen(SelectedPage);
    }

    private bool IsOnScreen(SettingsViewModel page) => IsOpen && IsWindowShown && ReferenceEquals(page, SelectedPage);
}
