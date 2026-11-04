using System.Windows;
using System.Windows.Interop;
using ReplayBuffer.App.Theme;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App;

/// <summary>
/// The base of the App's own windows: excluded from capture as soon as it has a handle, before its first
/// frame (AD-15). <see cref="CaptureExclusion.ExcludeNewWindows"/>, which <see cref="ShellApp"/> installs
/// at start, is the net for every other window: plain WPF windows, popups and native dialogs. Its frame
/// follows the theme from the same moment (AD-23).
/// </summary>
internal class ShellWindow : Window
{
    /// <summary>Whether Fluent shows Mica behind this window; only the Main App's does (DESIGN.md).</summary>
    internal virtual bool UsesMica => false;

    protected override void OnSourceInitialized(EventArgs e)
    {
        CaptureExclusion.Apply(new WindowInteropHelper(this).Handle);
        ThemeService.Current?.Decorate(this);
        base.OnSourceInitialized(e);
    }
}
