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

    /// <summary>Restores and saves the window's size and place; null in tests, which place the window themselves.</summary>
    internal WindowPlacementKeeper? Placement { get; init; }

    /// <summary>The Settings window's own size and place, handed to it when it is first opened.</summary>
    internal WindowPlacementKeeper? SettingsPlacement { get; init; }

    /// <summary>The Settings window, made the first time it opens and kept, hidden, until Quit.</summary>
    internal SettingsWindow? Settings { get; private set; }

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

    /// <summary>Closes the window for good, on Quit, and the Settings window with it.</summary>
    public void CloseForQuit()
    {
        Settings?.CloseForQuit();
        _quitting = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        Placement?.Restore(this);
        base.OnSourceInitialized(e);
    }

    // Closing hides the window to the tray or quits; either way its windowed size is saved, so full screen is left first.
    protected override void OnClosing(CancelEventArgs e)
    {
        Model?.LeavePlayerFullScreen();
        Placement?.Save(this);
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
            old.Settings.OpenRequested -= ShowSettings;
        }

        if (e.NewValue is MainViewModel model)
        {
            model.PropertyChanged += OnModelPropertyChanged;
            model.Settings.OpenRequested += ShowSettings;
            ApplyFullScreen(model.IsPlayerFullScreen);
            ApplyNavigationWidth();
        }
    }

    // A hidden window leaves full screen and takes the Settings window with it; the view model pauses what only a shown window needs.
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        if (!IsVisible)
        {
            model.LeavePlayerFullScreen();
            Settings?.Hide();
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
    }

    // The mouse's Back button closes a drill-in item page.
    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && Model?.GoBack() == true)
        {
            e.Handled = true;
        }
    }

    // Choosing the Library again goes back to its list.
    private void OnNavClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit && ItemsControl.ContainerFromElement(Nav, hit) is ListBoxItem)
        {
            Model?.ReselectLibrary();
        }
    }

    // Enter or Space on the Library item does what a click on it does.
    private void OnNavKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && e.OriginalSource is ListBoxItem && Model is { CurrentDrillIn.IsOpen: true } model)
        {
            model.ReselectLibrary();
            e.Handled = true;
        }
    }

    // The click has already flipped the toggle, so it is set back to checked while the Settings window is open.
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            model.OpenSettings();
            SettingsButton.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        }
    }

    // The Settings window is owned by this one, made once and brought to the front on every request.
    private void ShowSettings()
    {
        Settings ??= new SettingsWindow { DataContext = Model!.Settings, Owner = this, Placement = SettingsPlacement };
        Settings.Open();
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
