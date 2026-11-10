namespace Encore.Platform.Windows;

public sealed partial class GameMonitor
{
    /// <summary>Executables the fullscreen rule never detects as games, matched on the file name, ignoring case.</summary>
    public static IReadOnlySet<string> KnownNonGames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Browsers.
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe", "vivaldi.exe", "iexplore.exe",

        // Media players.
        "vlc.exe", "mpc-hc.exe", "mpc-hc64.exe", "mpc-be.exe", "mpc-be64.exe", "mpv.exe", "wmplayer.exe", "video.ui.exe",
        "microsoft.media.player.exe", "potplayer.exe", "potplayer64.exe", "potplayermini.exe", "potplayermini64.exe", "ffplay.exe",

        // Chat, the shell and Office.
        "discord.exe", "explorer.exe", "applicationframehost.exe", "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe",
        "onenote.exe", "msaccess.exe", "mspub.exe", "visio.exe", "teams.exe", "ms-teams.exe",

        // Encore itself, wherever it runs from (a dev build isn't under the install folder).
        "encore.app.exe", "encore.engine.exe",

        // ReplayBuffer, the app before its rebrand to Encore, if an old copy still runs.
        "replaybuffer.app.exe", "replaybuffer.engine.exe",
    };
}
