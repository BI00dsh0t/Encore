using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ReplayBuffer.Platform.Desktop;

/// <summary>
/// The Win32 side of the Overlay window (AD-2): extended styles that keep it topmost, click-through
/// and never activated, and placement that never takes the foreground from the game.
/// </summary>
public static class OverlayWindowStyle
{
    /// <summary>
    /// <c>WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>.
    /// </summary>
    public const uint Styles = (uint)(WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TRANSPARENT
        | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);

    private static readonly HWND Topmost = new(-1);

    /// <summary>ORs <see cref="Styles"/> into the window's extended style.</summary>
    /// <exception cref="Win32Exception">The window doesn't exist.</exception>
    public static void Apply(nint hwnd)
    {
        var style = ExtendedStyle(hwnd);
        Marshal.SetLastPInvokeError(0);
        if (PInvoke.SetWindowLongPtr(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(style | Styles)) == 0
            && Marshal.GetLastPInvokeError() != 0)
        {
            throw new Win32Exception();
        }
    }

    /// <summary>The window's extended style.</summary>
    /// <exception cref="Win32Exception">The window doesn't exist.</exception>
    public static uint ExtendedStyle(nint hwnd)
    {
        Marshal.SetLastPInvokeError(0);
        var style = PInvoke.GetWindowLongPtr(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw new Win32Exception();
        }

        return (uint)style;
    }

    /// <summary>
    /// Moves the window to <paramref name="x"/>, <paramref name="y"/> (physical pixels), sizes it,
    /// puts it on top of the topmost band and shows it, all without activating it.
    /// </summary>
    /// <exception cref="Win32Exception">The window doesn't exist.</exception>
    public static void ShowWithoutActivation(nint hwnd, int x, int y, int width, int height)
    {
        if (!PInvoke.SetWindowPos(new HWND(hwnd), Topmost, x, y, width, height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW))
        {
            throw new Win32Exception();
        }
    }

    /// <summary>The window's bounds (<c>GetWindowRect</c>): left, top, right and bottom.</summary>
    /// <exception cref="Win32Exception">The window doesn't exist.</exception>
    public static (int Left, int Top, int Right, int Bottom) Bounds(nint hwnd) =>
        PInvoke.GetWindowRect(new HWND(hwnd), out var rect)
            ? (rect.left, rect.top, rect.right, rect.bottom)
            : throw new Win32Exception();

    /// <summary>The foreground window's handle, or 0 when there is none.</summary>
    public static nint ForegroundWindow() => PInvoke.GetForegroundWindow();
}
