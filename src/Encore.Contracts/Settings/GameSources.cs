namespace Encore.Contracts.Settings;

/// <summary>How Game Detection found a game, recorded on its Game Profile and its <c>game.detected</c>. Additive.</summary>
public static class GameSources
{
    /// <summary>In Windows' own game list.</summary>
    public const string WindowsList = "windowsList";

    /// <summary>Added by hand, in <c>manualGames</c>.</summary>
    public const string Manual = "manual";

    public const string Steam = "steam";

    public const string Xbox = "xbox";

    public const string Epic = "epic";

    public const string Gog = "gog";

    public const string Ubisoft = "ubisoft";

    /// <summary>Only by its window: borderless or fullscreen, and in no list or store folder.</summary>
    public const string Fullscreen = "fullscreen";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        WindowsList, Manual, Steam, Xbox, Epic, Gog, Ubisoft, Fullscreen,
    };
}
