using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Controls;

namespace Encore.Platform.Desktop;

/// <summary>The DWM side of a themed window: the Mica backdrop and the title bar's colours. A closed window is ignored.</summary>
public static class WindowFrame
{
    /// <summary>DWM's "use the system's own colour" for the caption, border and text attributes.</summary>
    private const uint DefaultColor = 0xFFFFFFFF;

    /// <summary>Whether Windows draws system backdrops (Windows 11 22H2, build 22621, and later).</summary>
    public static bool SupportsMica { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>Turns Mica on or off; with it on, WPF content must leave its background transparent where Mica should show.</summary>
    public static unsafe void SetMica(nint window, bool on)
    {
        var margins = on ? new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 } : default;
        _ = PInvoke.DwmExtendFrameIntoClientArea(new HWND(window), &margins);
        var type = on ? DWM_SYSTEMBACKDROP_TYPE.DWMSBT_MAINWINDOW : DWM_SYSTEMBACKDROP_TYPE.DWMSBT_NONE;
        _ = PInvoke.DwmSetWindowAttribute(
            new HWND(window), DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, &type, sizeof(DWM_SYSTEMBACKDROP_TYPE));
    }

    /// <summary>Whether the window has the Mica backdrop.</summary>
    public static unsafe bool HasMica(nint window)
    {
        DWM_SYSTEMBACKDROP_TYPE type = default;
        return PInvoke.DwmGetWindowAttribute(
                new HWND(window), DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, &type, sizeof(DWM_SYSTEMBACKDROP_TYPE)).Succeeded
            && type == DWM_SYSTEMBACKDROP_TYPE.DWMSBT_MAINWINDOW;
    }

    /// <summary>Colours the system title bar and its text, each as 0xRRGGBB; null puts both back to the system's own.</summary>
    public static unsafe void SetCaptionColors(nint window, (uint Caption, uint Text)? colors)
    {
        var caption = colors is { } chosen ? ColorRef(chosen.Caption) : DefaultColor;
        var text = colors is { } chosenText ? ColorRef(chosenText.Text) : DefaultColor;
        _ = PInvoke.DwmSetWindowAttribute(new HWND(window), DWMWINDOWATTRIBUTE.DWMWA_CAPTION_COLOR, &caption, sizeof(uint));
        _ = PInvoke.DwmSetWindowAttribute(new HWND(window), DWMWINDOWATTRIBUTE.DWMWA_TEXT_COLOR, &text, sizeof(uint));
    }

    // COLORREF is 0x00BBGGRR; swapping red and blue goes both ways.
    private static uint ColorRef(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
}
