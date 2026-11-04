using System.ComponentModel;

namespace ReplayBuffer.App;

/// <summary>The Main App window. Closing it only hides it; the App lives in the tray until Quit (AD-16).</summary>
internal partial class MainWindow
{
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
