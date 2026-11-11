using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>How Encore knows a game, in words: the line under its name in Games and on its page.</summary>
internal static class GameSourceText
{
    /// <summary>"Added by you" for a hand-added game, else the store or list that found it; an older profile has none.</summary>
    public static string Describe(string? source, bool isManual)
    {
        if (isManual)
        {
            return "Added by you";
        }

        return source switch
        {
            GameSources.Steam => "Steam game",
            GameSources.Xbox => "Xbox game",
            GameSources.Epic => "Epic game",
            GameSources.Gog => "GOG game",
            GameSources.Ubisoft => "Ubisoft game",
            GameSources.WindowsList => "In Windows' game list",
            GameSources.Fullscreen => "Found in fullscreen",
            _ => "Detected automatically",
        };
    }
}
