namespace Encore.App;

/// <summary>How wide the Main App's nav pane is: full, narrower in a small window, or a rail of glyphs when collapsed.</summary>
internal static class NavPaneLayout
{
    /// <summary>Below this window width the expanded pane narrows and long titles trim.</summary>
    public const double NarrowWindowWidth = 640;

    public const double FullWidth = 200;

    public const double NarrowWidth = 160;

    public const double RailWidth = 58;

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
}
