using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Encore.App;

/// <summary>What the tray icon sits on: a light or a dark taskbar, and whether high contrast is on.</summary>
internal readonly record struct TaskbarLook(bool IsLight, bool IsHighContrast)
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string LightTaskbarValue = "SystemUsesLightTheme";

    /// <summary>The look now; in high contrast the taskbar takes the system window colour, so its lightness decides.</summary>
    public static TaskbarLook Current()
    {
        if (SystemParameters.HighContrast)
        {
            return new TaskbarLook(IsLightColor(SystemColors.WindowColor), IsHighContrast: true);
        }

        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        var usesLight = key?.GetValue(LightTaskbarValue) is int value && value != 0;
        return new TaskbarLook(usesLight, IsHighContrast: false);
    }

    private static bool IsLightColor(Color color) => 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 127;
}
