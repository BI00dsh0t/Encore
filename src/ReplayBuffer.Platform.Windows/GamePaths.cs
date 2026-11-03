using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace ReplayBuffer.Platform.Windows;

/// <summary>Game executable paths.</summary>
public static unsafe class GamePaths
{
    private const string VerbatimPrefix = @"\\?\";
    private const string VerbatimUncPrefix = @"\\?\UNC\";
    private const uint FileReadAttributes = 0x80;

    /// <summary>
    /// The Game Profile id of an executable (AD-18), and the only place one is made: the file's final
    /// path from <c>GetFinalPathNameByHandle(FILE_NAME_NORMALIZED | VOLUME_NAME_DOS)</c>, with
    /// <c>\\?\</c> stripped (<c>\\?\UNC\</c> becomes <c>\\</c>), in lowercase (invariant).
    /// </summary>
    /// <exception cref="Win32Exception">The file couldn't be opened or its final path read.</exception>
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

    private static string StripVerbatim(string path)
    {
        if (path.StartsWith(VerbatimUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[VerbatimUncPrefix.Length..];
        }

        return path.StartsWith(VerbatimPrefix, StringComparison.Ordinal) ? path[VerbatimPrefix.Length..] : path;
    }
}
