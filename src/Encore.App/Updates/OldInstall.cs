using System.Diagnostics;
using System.IO;

namespace Encore.App.Updates;

/// <summary>The copy the old install script put in Programs\Encore, which the installer replaces.</summary>
internal static class OldInstall
{
    /// <summary>Once the installer's copy runs, offers to delete the old copy when nothing runs from it.</summary>
    public static void OfferRemoval(bool isInstallerCopy, Func<string, bool> ask, Action<string> log)
    {
        var folder = InstallLayout.OldInstallFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (!isInstallerCopy || !File.Exists(Path.Combine(folder, "Encore.App.exe")) || IsRunningFrom(folder))
        {
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
