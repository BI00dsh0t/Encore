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

    private bool _quitting;

    internal override bool UsesMica => true;

    public MainWindow()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (DataContext is MainViewModel model)
            {
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

    /// <summary>Whether the nav pane is at its narrow width.</summary>
    internal bool IsNavNarrow => NavColumn.Width.Value < NavWidth;

    // Ctrl+Tab and Ctrl+Shift+Tab step through the nav from anywhere, as the old TabControl did. On the
    // bubbling KeyDown, so the Hotkeys page, which takes every key while capturing, keeps them.
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Story 10.10: Esc (after a control's own) and Alt+Left close a drill-in item page.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
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
        (Nav.ItemContainerGenerator.ContainerFromItem(Nav.SelectedItem) as ListBoxItem)?.Focus();
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
            && DataContext is MainViewModel model
            && ReferenceEquals(item, model.SelectedNavItem))
        {
            model.CurrentDrillIn?.Close();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < NarrowWidth;
        NavColumn.Width = new GridLength(narrow ? NarrowNavWidth : NavWidth);
        Nav.Tag = narrow ? "Narrow" : null;
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
