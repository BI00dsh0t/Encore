using System.Windows;
using System.Windows.Interop;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App;

/// <summary>The base of every App window: excluded from capture as soon as it has a handle (AD-15).</summary>
internal class ShellWindow : Window
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        CaptureExclusion.Apply(new WindowInteropHelper(this).Handle);
        base.OnSourceInitialized(e);
    }
}
