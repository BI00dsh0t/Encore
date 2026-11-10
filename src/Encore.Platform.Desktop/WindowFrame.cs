using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Controls;

namespace Encore.Platform.Desktop;

/// <summary>
/// The DWM side of a themed window (AD-23): the Mica backdrop behind the Main App under Fluent, and the
/// caption colours Gaming dark sets on the system title bar. Calls on a window that is gone do nothing.
/// </summary>
public static class WindowFrame
{
    /// <summary>DWM's "use the system's own colour" for the caption, border and text attributes.</summary>
    private const uint DefaultColor = 0xFFFFFFFF;

    /// <summary>Whether Windows draws system backdrops (Windows 11 22H2, build 22621, and later).</summary>
    public static bool SupportsMica { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>
    /// Turns Mica on or off behind the whole window. With it on, the frame is extended over the client
    /// area, so WPF's content must leave its own background transparent where Mica should show.
    /// </summary>
    public static unsafe void SetMica(nint hwnd, bool on)
    {
        var margins = on ? new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 } : default;
        _ = PInvoke.DwmExtendFrameIntoClientArea(new HWND(hwnd), &margins);
        var type = on ? DWM_SYSTEMBACKDROP_TYPE.DWMSBT_MAINWINDOW : DWM_SYSTEMBACKDROP_TYPE.DWMSBT_NONE;
        _ = PInvoke.DwmSetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, &type, sizeof(DWM_SYSTEMBACKDROP_TYPE));
    }

    /// <summary>Whether the window has the Mica backdrop.</summary>
    public static unsafe bool HasMica(nint hwnd)
    {
        DWM_SYSTEMBACKDROP_TYPE type = default;
        return PInvoke.DwmGetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, &type, sizeof(DWM_SYSTEMBACKDROP_TYPE)).Succeeded
            && type == DWM_SYSTEMBACKDROP_TYPE.DWMSBT_MAINWINDOW;
    }

    /// <summary>
    /// Colours the system title bar: the caption colour behind it and the text colour for
    /// the title, each as 0xRRGGBB. Null puts both back to the system's own.
    /// </summary>
    public static unsafe void SetCaptionColors(nint hwnd, (uint Caption, uint Text)? colors)
    {
        var caption = colors is { } c ? ColorRef(c.Caption) : DefaultColor;
        var text = colors is { } t ? ColorRef(t.Text) : DefaultColor;
        _ = PInvoke.DwmSetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_CAPTION_COLOR, &caption, sizeof(uint));
        _ = PInvoke.DwmSetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_TEXT_COLOR, &text, sizeof(uint));
    }

    // COLORREF is 0x00BBGGRR; swapping red and blue goes both ways.
    private static uint ColorRef(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
}
