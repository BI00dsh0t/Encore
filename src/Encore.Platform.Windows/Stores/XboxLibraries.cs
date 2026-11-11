using System.Buffers.Binary;
using System.Text;

namespace Encore.Platform.Windows.Stores;

/// <summary>The Xbox app's game folders, which each drive's <c>.GamingRoot</c> file names.</summary>
public sealed class XboxLibraries(IReadOnlyList<string>? driveRoots = null) : IStoreReader
{
    private const string FileName = ".GamingRoot";

    public GameStore Store => GameStore.Xbox;

    public IReadOnlyList<string> Folders(ICollection<string> unreadable)
    {
        List<string> folders = [];
        foreach (var root in driveRoots ?? FixedDrives())
        {
            var file = Path.Combine(root, FileName);
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                folders.AddRange(Names(File.ReadAllBytes(file)).Select(name => Path.Combine(root, name)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                unreadable.Add(file);
            }
        }

        return folders;
    }

    /// <summary>The folder names in a <c>.GamingRoot</c>: "RGBX", a count, then that many UTF-16 names, each ending in 0.</summary>
    public static IReadOnlyList<string> Names(byte[] file)
    {
        if (file.Length < 8 || Encoding.ASCII.GetString(file, 0, 4) != "RGBX")
        {
            throw new InvalidDataException("Not a .GamingRoot file.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        var text = Encoding.Unicode.GetString(file, 8, (file.Length - 8) & ~1);
        var names = text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length < count)
        {
            throw new InvalidDataException("The .GamingRoot file names fewer folders than it says.");
        }

        return names[..(int)count];
    }

    private static IEnumerable<string> FixedDrives() =>
        DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => drive.RootDirectory.FullName);
}
