using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Encore.Platform.Desktop;

/// <summary>The pixel size of a notification-area icon, at the taskbar's DPI.</summary>
public static class TrayIconSize
{
    private const string TaskbarClass = "Shell_TrayWnd";

    /// <summary>The small-icon size for the taskbar's monitor, or for the system DPI when the taskbar can't be found.</summary>
    public static int Current()
    {
        var taskbar = PInvoke.FindWindow(TaskbarClass, null);
        var dpi = taskbar.IsNull ? 0 : PInvoke.GetDpiForWindow(taskbar);
        if (dpi == 0)
        {
            dpi = PInvoke.GetDpiForSystem();
        }

        return PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, dpi);
    }
}
