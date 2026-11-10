using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Encore.App;

/// <summary>The Main App window. Closing it only hides it; the App lives in the tray until Quit.</summary>
internal partial class MainWindow
{
    private bool _quitting;

    // The window as it was before the player went full screen; null while not.
    private (WindowStyle Style, ResizeMode Resize, WindowState State, Thickness PageMargin)? _beforeFullScreen;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    internal override bool UsesMica => true;

    /// <summary>Whether the window is in the player's full screen: for tests.</summary>
    internal bool IsFullScreen => _beforeFullScreen is not null;

    private MainViewModel? Model => DataContext as MainViewModel;

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

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
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
    }

    // A hidden window leaves full screen, and the view model pauses what only a shown window needs.
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        if (!IsVisible)
        {
            model.LeavePlayerFullScreen();
        }

        model.IsWindowShown = IsVisible;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsNavigationExpanded))
        {
            ApplyNavigationWidth();
        }

        if (e.PropertyName == nameof(MainViewModel.IsPlayerFullScreen) && sender is MainViewModel model)
        {
            ApplyFullScreen(model.IsPlayerFullScreen);
        }
    }

    // On the bubbling KeyDown, so a control's own Esc and a page capturing keys come first.
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var isBack = key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt;
        if ((key == Key.Escape && model.HandleEscape()) || (isBack && model.GoBack()))
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            StepThroughNavigation(model, backwards: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            e.Handled = true;
        }
    }

    // Ctrl+Tab and Ctrl+Shift+Tab: the next or previous page, with focus on the control that chose it.
    private void StepThroughNavigation(MainViewModel model, bool backwards)
    {
        model.NavigateBy(backwards ? -1 : 1);
        Nav.UpdateLayout();
        if (model.IsSettingsTabSelected)
        {
            SettingsSections.Focus();
        }
        else
        {
            (Nav.ItemContainerGenerator.ContainerFromItem(Nav.SelectedItem) as ListBoxItem)?.Focus();
        }
    }

    // The mouse's Back button closes a drill-in item page.
    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && Model?.GoBack() == true)
        {
            e.Handled = true;
        }
    }

    // Selecting the parent's nav item again goes back to its list.
    private void OnNavClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit
            && ItemsControl.ContainerFromElement(Nav, hit) is ListBoxItem { DataContext: NavItem item })
        {
            Model?.ReselectNavItem(item);
        }
    }

    // Enter or Space on the selected nav item does what a click on it does.
    private void OnNavKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space
            && e.OriginalSource is ListBoxItem { DataContext: NavItem item }
            && Model is { CurrentDrillIn.IsOpen: true } model)
        {
            model.ReselectNavItem(item);
            e.Handled = true;
        }
    }

    // On the preview event, so the current section is seen before the list commits and closes.
    private void OnSettingsSectionClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit
            && ItemsControl.ContainerFromElement(SettingsSections, hit) is ComboBoxItem { DataContext: NavItem item })
        {
            Model?.ReselectNavItem(item);
        }
    }

    // Enter or Space on the current section goes back to its list; any other key keeps the list's own behaviour.
    private void OnSettingsSectionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || !SettingsSections.IsDropDownOpen || Model is not { } model)
        {
            return;
        }

        if (ReferenceEquals(HighlightedSection(), model.SelectedNavItem))
        {
            model.ReselectNavItem(model.SelectedNavItem);
            SettingsSections.IsDropDownOpen = false;
            e.Handled = true;
        }
    }

    private NavItem? HighlightedSection() =>
        SettingsSections.Items.Cast<NavItem>().FirstOrDefault(item =>
            SettingsSections.ItemContainerGenerator.ContainerFromItem(item) is ComboBoxItem { IsHighlighted: true });

    // The click has already flipped the toggle, so it is set back to checked: Settings stays the open tab.
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            model.OpenSettings();
            SettingsButton.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyNavigationWidth();

    private void ApplyNavigationWidth()
    {
        if (IsFullScreen)
        {
            return;
        }

        var expanded = Model?.IsNavigationExpanded ?? true;
        NavColumn.Width = new GridLength(NavPaneLayout.WidthFor(expanded, ActualWidth));
        Nav.Tag = expanded ? null : NavPaneLayout.RailTag;
    }

    private void ApplyFullScreen(bool on)
    {
        if (on == IsFullScreen)
        {
            return;
        }

        if (on)
        {
            EnterFullScreen();
        }
        else
        {
            LeaveFullScreen();
        }
    }

    // Full screen for the player page: the window drops its frame and the shell's chrome.
    private void EnterFullScreen()
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
        ContentLayer.SetResourceReference(Border.BackgroundProperty, "EncorePlayerBackdropBrush");

        // Normal first, so the maximized borderless window covers the taskbar too.
        WindowState = System.Windows.WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = System.Windows.WindowState.Maximized;
    }

    // Leaving full screen puts back the frame, the chrome and the window state it had before.
    private void LeaveFullScreen()
    {
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
}
