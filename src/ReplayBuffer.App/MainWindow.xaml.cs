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
        if (e.Key != Key.Tab || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || DataContext is not MainViewModel model)
        {
            return;
        }

        model.NavigateBy((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
        Nav.UpdateLayout();
        (Nav.ItemContainerGenerator.ContainerFromItem(Nav.SelectedItem) as ListBoxItem)?.Focus();
        e.Handled = true;
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
