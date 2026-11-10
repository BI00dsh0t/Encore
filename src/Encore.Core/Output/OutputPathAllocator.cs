using System.Globalization;
using Encore.Contracts;
using Encore.Contracts.Settings;

namespace Encore.Core.Output;

/// <summary>The one producer of output paths: Output Folder, profile folder, then game name and time. Each is reserved on disk.</summary>
public sealed class OutputPathAllocator(TimeProvider time)
{
    /// <summary>The allocator on the system clock.</summary>
    public static OutputPathAllocator System { get; } = new(TimeProvider.System);

    /// <summary>Reserves a path for a profile, named from its live name and the press's local time; throws when unknown.</summary>
    public string Allocate(SettingsDocument document, string profileId, string outputFolder, long qpc)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentException.ThrowIfNullOrEmpty(outputFolder);

        if (!document.Profiles.TryGetValue(profileId, out var profile)
            || ProfileFolders.FolderOf(document, profileId) is not { } folder)
        {
            throw new ErrorCodeException(ErrorCodes.ProfileNotFound, $"No Game Profile '{profileId}'.");
        }

        return Reserve(Path.Combine(outputFolder, folder), profile.Name, LocalTimeOf(qpc));
    }

    /// <summary>Reserves a path for a game whose profile may not exist yet, then named after the game.</summary>
    public string AllocateForGame(SettingsDocument document, string? profileId, string gameName, string outputFolder, long qpc)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(gameName);
        ArgumentException.ThrowIfNullOrEmpty(outputFolder);

        if (profileId is not null && document.Profiles.ContainsKey(profileId))
        {
            return Allocate(document, profileId, outputFolder, qpc);
        }

        return Reserve(Path.Combine(outputFolder, PathNames.Sanitize(gameName, PathNames.GameFallback)), gameName, LocalTimeOf(qpc));
    }

    /// <summary>Reserves a Monitor Recording's path in the Monitor Recordings folder, from the press's local time.</summary>
    public string AllocateForMonitor(string outputFolder, long qpc)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFolder);
        return Reserve(Path.Combine(outputFolder, ProfileFolders.MonitorRecordings), "Monitor", LocalTimeOf(qpc));
    }

    /// <summary>Creates the folder and an empty file in it, adding <c>_2</c>, <c>_3</c>… while the name is taken.</summary>
    public string Reserve(string folder, string name, DateTime localTime)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(name);

        Directory.CreateDirectory(folder);
        var stamp = localTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var stem = $"{PathNames.Sanitize(name, PathNames.GameFallback)}_{stamp}";
        for (var number = 1; ; number++)
        {
            var path = Path.Combine(folder, number == 1 ? stem + ".mp4" : $"{stem}_{number}.mp4");
            try
            {
                new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Taken: try the next suffix.
            }
        }
    }

    // The local wall-clock time of the QPC timestamp qpc.
    private DateTime LocalTimeOf(long qpc) => (time.GetLocalNow() - time.GetElapsedTime(qpc)).DateTime;
}
