using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Encore.Platform.Windows;

public static unsafe class GamePaths
{
    private const string VerbatimPrefix = @"\\?\";
    private const string VerbatimUncPrefix = @"\\?\UNC\";
    private const uint FileReadAttributes = 0x80;

    /// <summary>The Game Profile id of an executable, made only here: its final path, lowercase, without the <c>\\?\</c> prefix.</summary>
    public static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Only the file's attributes are opened, never its contents.
        HANDLE file;
        fixed (char* name = path)
        {
            file = PInvoke.CreateFile(
                name,
                FileReadAttributes,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS,
                HANDLE.Null);
        }

        if (file.Value == (void*)-1)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"'{path}' couldn't be opened.");
        }

        try
        {
            var capacity = 512u;
            while (true)
            {
                var buffer = new char[capacity];
                uint length;
                fixed (char* chars = buffer)
                {
                    // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS are both 0.
                    length = PInvoke.GetFinalPathNameByHandle(file, chars, capacity, 0);
                }

                if (length == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"The final path of '{path}' couldn't be read.");
                }

                if (length < capacity)
                {
                    return StripVerbatim(new string(buffer, 0, (int)length)).ToLowerInvariant();
                }

                // Too small: length is the size needed, terminator included.
                capacity = length;
            }
        }
        finally
        {
            PInvoke.CloseHandle(file);
        }
    }

    /// <summary>Whether an exception is <see cref="Canonicalize"/> failing to read the file, which callers recover from.</summary>
    public static bool CanonicalizeFailed(Exception e) =>
        e is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException;

    /// <summary>A listed path as matched against Game Profile ids: canonical, or lowercase when the file is not on disk.</summary>
    public static string CanonicalOrLowercase(string path)
    {
        try
        {
            return Canonicalize(path);
        }
        catch (Exception e) when (CanonicalizeFailed(e))
        {
            return path.ToLowerInvariant();
        }
    }

    /// <summary>A game's display name: the ProductName of the executable's version resource, else its file name.</summary>
    public static string ProductName(string path, string? fallbackName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            var product = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim();
            if (!string.IsNullOrEmpty(product))
            {
                return product;
            }
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Named from the file instead.
        }

        var file = string.IsNullOrEmpty(fallbackName) ? path : fallbackName;
        var stem = Path.GetFileNameWithoutExtension(file);

        // A file named only ".exe" keeps its extension: a profile name is never empty.
        return string.IsNullOrWhiteSpace(stem) ? Path.GetFileName(file) : stem;
    }

    private static string StripVerbatim(string path)
    {
        if (path.StartsWith(VerbatimUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[VerbatimUncPrefix.Length..];
        }

        return path.StartsWith(VerbatimPrefix, StringComparison.Ordinal) ? path[VerbatimPrefix.Length..] : path;
    }
}
