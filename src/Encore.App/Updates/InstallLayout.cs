using System.IO;

namespace Encore.App.Updates;

/// <summary>Where the installer puts the App: a version folder that updates replace, beside a launcher that stays put.</summary>
internal static class InstallLayout
{
    /// <summary>The folder the installer replaces on each update.</summary>
    public const string CurrentFolderName = "current";

    /// <summary>The installer's own updater, beside the launcher.</summary>
    public const string UpdaterFileName = "Update.exe";

    /// <summary>The folder the robocopy install used before the installer existed.</summary>
    public static string OldInstallFolder(string localAppData) => Path.Combine(localAppData, "Programs", "Encore");

    /// <summary>The path Windows should start: the launcher for an installed App, else the App itself.</summary>
    public static string LaunchPath(string appPath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(appPath);
        ArgumentNullException.ThrowIfNull(fileExists);
        var folder = Path.GetDirectoryName(appPath);
        var root = Path.GetDirectoryName(folder);
        bool isInCurrentFolder = string.Equals(Path.GetFileName(folder), CurrentFolderName, StringComparison.OrdinalIgnoreCase);
        if (root is null || !isInCurrentFolder || !fileExists(Path.Combine(root, UpdaterFileName)))
        {
            return appPath;
        }

        return Path.Combine(root, Path.GetFileName(appPath));
    }
}
