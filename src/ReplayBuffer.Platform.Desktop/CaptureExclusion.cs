using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ReplayBuffer.Platform.Desktop;

/// <summary>
/// Keeps the App's windows out of every capture, its own included (AD-15):
/// <c>WDA_EXCLUDEFROMCAPTURE</c> on a top-level window of this process.
/// </summary>
public static unsafe class CaptureExclusion
{
    /// <exception cref="Win32Exception">The window isn't a top-level window of this process.</exception>
    public static void Apply(nint hwnd)
    {
        if (!PInvoke.SetWindowDisplayAffinity(new HWND(hwnd), WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
        {
            throw new Win32Exception();
        }
    }

    /// <summary>Whether the window is excluded from capture.</summary>
    /// <exception cref="Win32Exception">The window doesn't exist.</exception>
    public static bool IsApplied(nint hwnd)
    {
        uint affinity;
        if (!PInvoke.GetWindowDisplayAffinity(new HWND(hwnd), &affinity))
        {
            throw new Win32Exception();
        }

        return affinity == (uint)WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE;
    }
}
