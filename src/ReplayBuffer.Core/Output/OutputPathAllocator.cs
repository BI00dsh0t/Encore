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
    private const string Fallback = "Game";

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

        var localTime = (time.GetLocalNow() - time.GetElapsedTime(qpc)).DateTime;
        return Reserve(Path.Combine(outputFolder, folder), profile.Name, localTime);
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
        var stem = $"{PathNames.Sanitize(name, Fallback)}_{localTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}";
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
}
