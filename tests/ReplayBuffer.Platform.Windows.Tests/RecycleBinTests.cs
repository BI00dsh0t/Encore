using System.Security.Principal;
using System.Text;

namespace ReplayBuffer.Platform.Windows.Tests;

public sealed class RecycleBinTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-recycle-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void RecycleMovesTheFileToTheUsersRecycleBinRestorableToItsPath()
    {
        var path = Path.Combine(_folder, "clip-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.True(RecycleBin.Recycle(path));
        Assert.False(File.Exists(path));

        // The $I file (v2) names the original path; its $R twin holds the bytes. Both are taken back out.
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var bin = Path.Combine(Path.GetPathRoot(path)!, "$Recycle.Bin", sid);
        var info = Directory.EnumerateFiles(bin, "$I*").Single(i => string.Equals(OriginalPath(i), path, StringComparison.OrdinalIgnoreCase));
        var data = Path.Combine(bin, "$R" + Path.GetFileName(info)[2..]);
        File.Move(data, path);
        File.Delete(info);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
    }

    // 8-byte version, 8-byte size, 8-byte FILETIME, 4-byte char count, then the UTF-16 path.
    private static string? OriginalPath(string info)
    {
        try
        {
            var bytes = File.ReadAllBytes(info);
            if (bytes.Length < 28 || BitConverter.ToInt64(bytes, 0) != 2)
            {
                return null;
            }

            var chars = BitConverter.ToInt32(bytes, 24);
            return Encoding.Unicode.GetString(bytes, 28, Math.Min(chars * 2, bytes.Length - 28)).TrimEnd('\0');
        }
        catch (IOException)
        {
            return null;
        }
    }
}
