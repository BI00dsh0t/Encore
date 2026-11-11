using System.Windows;
using Encore.Contracts.Settings;

namespace Encore.App.Theme;

/// <summary>The shell's dictionaries: both themes define the same keys, so the Main App's XAML names one key.</summary>
internal static class ShellTheme
{
    /// <summary>Puts a theme's shell dictionary in the merged list, replacing the other's, before the theme's own dictionary.</summary>
    public static void Place(IList<ResourceDictionary> merged, AppTheme theme)
    {
        var at = IndexOfShell(merged);
        if (at >= 0 && merged[at] is ShellGamingDark == (theme == AppTheme.GamingDark))
        {
            return;
        }

        ResourceDictionary next = theme == AppTheme.GamingDark ? new ShellGamingDark() : new ShellFluent();
        if (at >= 0)
        {
            merged[at] = next;
        }
        else
        {
            merged.Insert(Math.Max(0, merged.Count - 1), next);
        }
    }

    private static int IndexOfShell(IList<ResourceDictionary> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i] is ShellFluent or ShellGamingDark)
            {
                return i;
            }
        }

        return -1;
    }
}
