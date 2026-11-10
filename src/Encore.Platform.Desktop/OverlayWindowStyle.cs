using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Encore.Platform.Desktop;

/// <summary>The Overlay window's Win32 styles and placement: topmost, click-through and never taking focus from the game.</summary>
public static class OverlayWindowStyle
{
    /// <summary>Topmost, click-through, layered, never activated, and hidden from the taskbar.</summary>
    public const uint Styles = (uint)(WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TRANSPARENT
        | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);

    private static readonly HWND Topmost = new(-1);

    /// <summary>ORs <see cref="Styles"/> into the window's extended style; throws Win32Exception when the window doesn't exist.</summary>
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

    /// <summary>The window's extended style; throws Win32Exception when the window doesn't exist.</summary>
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

    /// <summary>Moves, sizes and shows the window on top without activating it; the position is in physical pixels.</summary>
    public static void ShowWithoutActivation(nint hwnd, int x, int y, int width, int height)
    {
        if (!PInvoke.SetWindowPos(new HWND(hwnd), Topmost, x, y, width, height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW))
        {
            throw new Win32Exception();
        }
    }

    /// <summary>The window's bounds: left, top, right and bottom; throws Win32Exception when the window doesn't exist.</summary>
    public static (int Left, int Top, int Right, int Bottom) Bounds(nint hwnd) =>
        PInvoke.GetWindowRect(new HWND(hwnd), out var rect)
            ? (rect.left, rect.top, rect.right, rect.bottom)
            : throw new Win32Exception();

    /// <summary>The foreground window's handle, or 0 when there is none.</summary>
    public static nint ForegroundWindow() => PInvoke.GetForegroundWindow();
}
