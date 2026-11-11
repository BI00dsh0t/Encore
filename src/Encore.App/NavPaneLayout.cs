namespace Encore.App;

/// <summary>How wide the Main App's nav pane is, full, narrower or a rail of glyphs, and when Settings' sections are a rail.</summary>
internal static class NavPaneLayout
{
    /// <summary>Below this window width the expanded pane narrows and long titles trim.</summary>
    public const double NarrowWindowWidth = 640;

    public const double FullWidth = 200;

    public const double NarrowWidth = 160;

    public const double RailWidth = 58;

    /// <summary>The Settings overlay's section list at full width.</summary>
    public const double SettingsSectionsWidth = 240;

    /// <summary>Below this panel width the Settings overlay's sections show as icons.</summary>
    public const double NarrowSettingsPanelWidth = 640;

    /// <summary>The nav list's Tag while the pane is a rail; the nav item template hides its label on it.</summary>
    public const string RailTag = "Rail";

    public static double WidthFor(bool expanded, double windowWidth)
    {
        if (!expanded)
        {
            return RailWidth;
        }

        return windowWidth < NarrowWindowWidth ? NarrowWidth : FullWidth;
    }

    /// <summary>Whether the Settings overlay's sections show as icons in a panel this wide.</summary>
    public static bool SettingsSectionsAsRail(double panelWidth) => panelWidth < NarrowSettingsPanelWidth;
}
