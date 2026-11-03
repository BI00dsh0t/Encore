using System.Globalization;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Core.Output;

/// <summary>
/// The one producer of output paths (R26, AD-21):
/// <c>&lt;Output Folder&gt;\&lt;profile folder&gt;\&lt;Game&gt;_&lt;yyyy-MM-dd_HH-mm-ss&gt;.mp4</c>.
/// Each path is reserved on disk before it is returned; nothing is cached.
/// </summary>
public sealed class OutputPathAllocator(TimeProvider time)
{
    /// <summary>The allocator on the system clock.</summary>
    public static OutputPathAllocator System { get; } = new(TimeProvider.System);

    /// <summary>
    /// Reserves a path for profile <paramref name="profileId"/> in <paramref name="outputFolder"/>,
    /// named from the profile's live name and the local time of <paramref name="qpc"/>.
    /// </summary>
    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.ProfileNotFound"/>: the document has no such profile.</exception>
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

    /// <summary>
    /// Reserves a path for a game whose Game Profile may not exist yet: <see cref="Allocate"/> when
    /// <paramref name="document"/> holds profile <paramref name="profileId"/>, else a folder and file
    /// named after <paramref name="gameName"/> (the executable's name until profiles name it).
    /// </summary>
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

    /// <summary>
    /// Creates <paramref name="folder"/> and an empty <c>&lt;name&gt;_&lt;localTime&gt;.mp4</c> in it,
    /// adding <c>_2</c>, <c>_3</c>… while the path is taken, and returns the path created.
    /// </summary>
    public string Reserve(string folder, string name, DateTime localTime)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(name);

        Directory.CreateDirectory(folder);
        var stem = $"{PathNames.Sanitize(name, PathNames.GameFallback)}_{localTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}";
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(folder, n == 1 ? stem + ".mp4" : $"{stem}_{n}.mp4");
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
