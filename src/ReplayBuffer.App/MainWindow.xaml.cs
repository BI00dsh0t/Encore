using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReplayBuffer.App;

/// <summary>The Main App window. Closing it only hides it; the App lives in the tray until Quit (AD-16).</summary>
internal partial class MainWindow
{
    /// <summary>Below this width the nav pane narrows and long titles trim (EXPERIENCE.md).</summary>
    public const double NarrowWidth = 640;

    private const double NavWidth = 200;
    private const double NarrowNavWidth = 160;
    private const double RailWidth = 58;

    private bool _quitting;

    // The window as it was before the player went full screen (story 14.9); null while not.
    private (WindowStyle Style, ResizeMode Resize, WindowState State, Thickness PageMargin)? _beforeFullScreen;

    internal override bool UsesMica => true;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old)
            {
                old.PropertyChanged -= OnModelPropertyChanged;
            }

            if (e.NewValue is MainViewModel model)
            {
                model.PropertyChanged += OnModelPropertyChanged;
                ApplyFullScreen(model.IsPlayerFullScreen);
                ApplyNavigationWidth();
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (DataContext is MainViewModel model)
            {
                if (!IsVisible)
                {
                    model.LeavePlayerFullScreen();
                }

                model.IsWindowShown = IsVisible;
            }
        };
    }

    /// <summary>Shows the window and brings it to the front.</summary>
    public void Open()
    {
        Show();
        if (WindowState == System.Windows.WindowState.Minimized)
        {
            WindowState = System.Windows.WindowState.Normal;
        }

        Activate();
    }

    /// <summary>Closes the window for good, on Quit.</summary>
    public void CloseForQuit()
    {
        _quitting = true;
        Close();
    }

    // Ctrl+Tab and Ctrl+Shift+Tab step through the nav from anywhere, as the old TabControl did. On the
    // bubbling KeyDown, so the Hotkeys page, which takes every key while capturing, keeps them.
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Story 10.10: Esc (after a control's own) and Alt+Left close a drill-in item page.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Story 14.9: in full screen, Esc only leaves it; the player page stays open.
        if (key == Key.Escape && DataContext is MainViewModel { IsPlayerFullScreen: true } fullScreen)
        {
            fullScreen.LeavePlayerFullScreen();
            e.Handled = true;
            return;
        }

        if (DataContext is MainViewModel { CurrentDrillIn: { IsOpen: true } drillIn }
            && (key == Key.Escape || (key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)))
        {
            drillIn.Close();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Tab || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || DataContext is not MainViewModel model)
        {
            return;
        }

        model.NavigateBy((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
        Nav.UpdateLayout();
        if (model.IsSettingsTabSelected) { SettingsSections.Focus(); }
        else { (Nav.ItemContainerGenerator.ContainerFromItem(Nav.SelectedItem) as ListBoxItem)?.Focus(); }
        e.Handled = true;
    }

    // The mouse's Back button closes a drill-in item page.
    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && DataContext is MainViewModel { CurrentDrillIn: { IsOpen: true } drillIn })
        {
            drillIn.Close();
            e.Handled = true;
        }
    }

    // Selecting the parent's nav item again goes back to its list.
    private void OnNavClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit
            && ItemsControl.ContainerFromElement(Nav, hit) is ListBoxItem { DataContext: NavItem item }
            && DataContext is MainViewModel model)
        {
            model.ReselectNavItem(item);
        }
    }

    // Enter or Space on the selected nav item does what a click on it does.
    private void OnNavKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Enter || e.Key == Key.Space)
            && e.OriginalSource is ListBoxItem { DataContext: NavItem item }
            && DataContext is MainViewModel { CurrentDrillIn.IsOpen: true } model)
        {
            model.ReselectNavItem(item);
            e.Handled = true;
        }
    }

    // Preview observes the current section before ComboBox commits/dismisses its popup.
    private void OnSettingsSectionClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit
            && ItemsControl.ContainerFromElement(SettingsSections, hit) is ComboBoxItem { DataContext: NavItem item }
            && DataContext is MainViewModel model)
        {
            model.ReselectNavItem(item);
        }
    }

    private void OnSettingsSectionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || !SettingsSections.IsDropDownOpen
            || DataContext is not MainViewModel model) { return; }
        var highlighted = SettingsSections.Items.Cast<NavItem>()
            .FirstOrDefault(item => SettingsSections.ItemContainerGenerator.ContainerFromItem(item) is ComboBoxItem { IsHighlighted: true });
        if (ReferenceEquals(highlighted, model.SelectedNavItem))
        {
            model.ReselectNavItem(model.SelectedNavItem);
            SettingsSections.IsDropDownOpen = false;
            e.Handled = true;
        }
        // Escape and other section choices keep ComboBox's normal dismissal/selection behavior.
    }

    /// <summary>Whether the window is in the player's full screen: for tests.</summary>
    internal bool IsFullScreen => _beforeFullScreen is not null;

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsNavigationExpanded)) { ApplyNavigationWidth(); }
        if (e.PropertyName == nameof(MainViewModel.IsPlayerFullScreen) && sender is MainViewModel model)
        {
            ApplyFullScreen(model.IsPlayerFullScreen);
        }
    }

    // Story 14.9: the player page fills the screen. The window drops its frame and covers the monitor, and the
    // nav, status line, page header and page padding go; leaving puts each back as it was.
    private void ApplyFullScreen(bool on)
    {
        if (on == IsFullScreen)
        {
            return;
        }

        if (on)
        {
            _beforeFullScreen = (WindowStyle, ResizeMode, WindowState, Page.Margin);
            NavPane.Visibility = Visibility.Collapsed;
            NavColumn.Width = new GridLength(0);
            StatusHeader.Visibility = Visibility.Collapsed;
            PageHeader.Visibility = Visibility.Collapsed;
            ContentPanel.Margin = new Thickness(0);
            Page.SetCurrentValue(MarginProperty, new Thickness(0));
            ContentLayer.BorderThickness = new Thickness(0);
            ContentLayer.CornerRadius = new CornerRadius(0);
            ContentLayer.SetResourceReference(Border.BackgroundProperty, "RbPlayerBackdropBrush");

            // Normal first, so the maximized borderless window covers the taskbar too.
            WindowState = System.Windows.WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = System.Windows.WindowState.Maximized;
            return;
        }

        var (style, resize, state, pageMargin) = _beforeFullScreen!.Value;
        _beforeFullScreen = null;
        WindowState = System.Windows.WindowState.Normal;
        WindowStyle = style;
        ResizeMode = resize;
        WindowState = state;
        NavPane.ClearValue(VisibilityProperty);
        StatusHeader.ClearValue(VisibilityProperty);
        PageHeader.ClearValue(VisibilityProperty);
        ContentPanel.ClearValue(MarginProperty);
        Page.SetCurrentValue(MarginProperty, pageMargin);
        ContentLayer.ClearValue(Border.BorderThicknessProperty);
        ContentLayer.ClearValue(Border.CornerRadiusProperty);
        ContentLayer.ClearValue(Border.BackgroundProperty);
        ApplyNavigationWidth();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsFullScreen)
        {
            return;
        }

        ApplyNavigationWidth();
    }

    private void ApplyNavigationWidth()
    {
        if (IsFullScreen) { return; }
        var expanded = DataContext is not MainViewModel model || model.IsNavigationExpanded;
        var narrow = ActualWidth < NarrowWidth;
        NavColumn.Width = new GridLength(expanded ? narrow ? NarrowNavWidth : NavWidth : RailWidth);
        Nav.Tag = !expanded ? "Rail" : narrow ? "Narrow" : null;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            if (model.IsSettingsTabSelected) { model.ReselectNavItem(model.SelectedNavItem); }
            model.IsSettingsTabSelected = true;
            SettingsButton.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
