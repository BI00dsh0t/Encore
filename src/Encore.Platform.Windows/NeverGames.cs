namespace Encore.Platform.Windows;

/// <summary>Programs that are never games: Windows' own, and a list of apps by file name. Only a listing detects them.</summary>
public static class NeverGames
{
    /// <summary>The Windows folder, lowercase with a trailing <c>\</c>: nothing under it is a game.</summary>
    public static string WindowsFolder { get; } = WithSeparator(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant());

    /// <summary>Executables that are never games, matched on the file name, ignoring case.</summary>
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Browsers.
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe", "vivaldi.exe", "iexplore.exe",
        "arc.exe", "zen.exe", "librewolf.exe", "waterfox.exe", "floorp.exe", "thorium.exe", "chromium.exe", "palemoon.exe",
        "seamonkey.exe", "maxthon.exe", "duckduckgo.exe",

        // Remote desktop and streaming clients.
        "mstsc.exe", "msrdc.exe", "anydesk.exe", "teamviewer.exe", "parsecd.exe", "moonlight.exe", "rustdesk.exe",
        "remoting_desktop.exe", "remoting_host.exe", "vncviewer.exe", "tvnviewer.exe", "strwinclt.exe",

        // Media players and media servers.
        "vlc.exe", "mpc-hc.exe", "mpc-hc64.exe", "mpc-be.exe", "mpc-be64.exe", "mpc-qt.exe", "mpv.exe", "wmplayer.exe",
        "video.ui.exe", "microsoft.media.player.exe", "potplayer.exe", "potplayer64.exe", "potplayermini.exe",
        "potplayermini64.exe", "ffplay.exe", "smplayer.exe", "kmplayer.exe", "kmplayer64x.exe", "plex.exe", "plex htpc.exe",
        "kodi.exe", "jellyfinmediaplayer.exe", "spotify.exe", "stremio.exe", "iptv-player.exe",

        // Chat and video calls.
        "discord.exe", "teams.exe", "ms-teams.exe", "zoom.exe", "slack.exe", "ciscocollabhost.exe", "webexmta.exe",
        "skype.exe", "signal.exe", "telegram.exe", "whatsapp.exe",

        // The shell, Office and PDF readers.
        "explorer.exe", "applicationframehost.exe", "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "onenote.exe",
        "msaccess.exe", "mspub.exe", "visio.exe", "acrobat.exe", "acrord32.exe", "sumatrapdf.exe", "foxitpdfreader.exe",
        "foxitphantompdf.exe", "pdfxedit.exe",

        // Screenshots, streaming, overlays and wallpapers.
        "snippingtool.exe", "screenclippinghost.exe", "sharex.exe", "greenshot.exe", "obs64.exe", "obs32.exe",
        "streamlabs obs.exe", "streamlabs desktop.exe", "losslessscaling.exe", "wallpaper32.exe", "wallpaper64.exe",
        "lively.exe", "nvidia overlay.exe", "gamebar.exe",

        // Encore itself, wherever it runs from (a dev build isn't under the install folder).
        "encore.app.exe", "encore.engine.exe",

        // ReplayBuffer, the app before its rebrand to Encore, if an old copy still runs.
        "replaybuffer.app.exe", "replaybuffer.engine.exe",
    };

    /// <summary>Whether a program is never a game: it is under the Windows folder, or its file name is in <see cref="Names"/>.</summary>
    public static bool Contains(string path) => Contains(path, WindowsFolder);

    /// <summary>The same rule with the Windows folder given, lowercase with a trailing <c>\</c>, as tests need.</summary>
    public static bool Contains(string path, string windowsFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return path.StartsWith(windowsFolder, StringComparison.OrdinalIgnoreCase) || Names.Contains(Path.GetFileName(path));
    }

    private static string WithSeparator(string folder) => folder.EndsWith('\\') ? folder : folder + '\\';
}
