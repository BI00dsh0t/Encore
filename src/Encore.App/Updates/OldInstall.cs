using System.Diagnostics;
using System.IO;

namespace Encore.App.Updates;

/// <summary>The copy the old install script put in Programs\Encore, which the installer replaces.</summary>
internal static class OldInstall
{
    // Only these file types were ever put there by the install script; anything else is the user's.
    private static readonly HashSet<string> InstallExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".json", ".pdb", ".xml", ".pri", ".winmd" };

    private static readonly HashSet<string> InstallFileNames =
        new(StringComparer.OrdinalIgnoreCase) { "LICENSE", "THIRD-PARTY-NOTICES.txt" };

    /// <summary>Once the installer's copy runs, offers to delete the old copy when nothing runs from it.</summary>
    public static void OfferRemoval(bool isInstallerCopy, Func<string, bool> ask, Action<string> log)
    {
        var folder = InstallLayout.OldInstallFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (!isInstallerCopy || !File.Exists(Path.Combine(folder, "Encore.App.exe")) || IsRunningFrom(folder))
        {
            return;
        }

        if (HasOtherFiles(folder))
        {
            log($"The old install in {folder} holds files the installer didn't put there, so it was left alone.");
            return;
        }

        var question = $"Encore now updates itself, so the old copy in {folder} is no longer needed. "
            + "Delete it? Your Clips and settings stay.";
        if (!ask(question))
        {
            return;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
            log($"Deleted the old install in {folder}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Couldn't delete the old install in {folder}: {e.Message}");
        }
    }

    /// <summary>Whether the folder holds any file the install script never wrote, such as a video or a note.</summary>
    internal static bool HasOtherFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(IsUsersFile);

    private static bool IsUsersFile(string path)
    {
        var name = Path.GetFileName(path);
        bool isInstallFile = InstallFileNames.Contains(name) || InstallExtensions.Contains(Path.GetExtension(name));
        return !isInstallFile;
    }

    private static bool IsRunningFrom(string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } image && image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Another user's or a protected process: not one of ours.
                }
            }
        }

        return false;
    }
}
