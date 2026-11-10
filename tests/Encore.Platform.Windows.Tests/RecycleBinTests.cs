using System.Security.Principal;
using System.Text;

namespace Encore.Platform.Windows.Tests;

public sealed class RecycleBinTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-recycle-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void RecycleMovesTheFileToTheUsersRecycleBinRestorableToItsPath()
    {
        var path = Path.Combine(_folder, "clip-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(path, [1, 2, 3]);

        // Held open without delete sharing (FFmpeg's length read): a sharing violation the actor retries.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(unchecked((int)0x80070020), Assert.Throws<IOException>(() => RecycleBin.Recycle(path)).HResult);
        }

        Assert.True(File.Exists(path));
        var restored = 0;
        try
        {
            Assert.True(RecycleBin.Recycle(path));
            Assert.False(File.Exists(path));
        }
        finally
        {
            // Always taken back out of the user's real Recycle Bin, even when an assert failed.
            restored = Restore(path);
        }

        Assert.Equal(1, restored);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
    }

    // The $I file (v2) names the original path; its $R twin holds the bytes. Moves each one recycled from
    // path back there and deletes its $I.
    private static int Restore(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var bin = Path.Combine(Path.GetPathRoot(path)!, "$Recycle.Bin", sid);
        var restored = 0;
        foreach (var info in Directory.EnumerateFiles(bin, "$I*").Where(i => string.Equals(OriginalPath(i), path, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var data = Path.Combine(bin, "$R" + Path.GetFileName(info)[2..]);
            File.Move(data, path, overwrite: true);
            File.Delete(info);
            restored++;
        }

        return restored;
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
