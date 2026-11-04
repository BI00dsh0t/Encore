using System.Windows;
using System.Windows.Interop;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App;

/// <summary>
/// The base of the App's own windows: excluded from capture as soon as it has a handle, before its first
/// frame (AD-15). <see cref="CaptureExclusion.ExcludeNewWindows"/>, which <see cref="ShellApp"/> installs
/// at start, is the net for every other window: plain WPF windows, popups and native dialogs.
/// </summary>
internal class ShellWindow : Window
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        CaptureExclusion.Apply(new WindowInteropHelper(this).Handle);
        base.OnSourceInitialized(e);
    }
}
