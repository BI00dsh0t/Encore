using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Encore.Platform.Windows;

/// <summary>A file handle's I/O priority hint.</summary>
public static unsafe class FileIoPriority
{
    /// <summary>Marks the handle low priority, so its writes queue behind the game's I/O. A failure is ignored.</summary>
    public static void SetLow(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var info = new FILE_IO_PRIORITY_HINT_INFO { PriorityHint = PRIORITY_HINT.IoPriorityHintLow };
            PInvoke.SetFileInformationByHandle(
                (HANDLE)handle.DangerousGetHandle(),
                FILE_INFO_BY_HANDLE_CLASS.FileIoPriorityHintInfo,
                &info,
                (uint)sizeof(FILE_IO_PRIORITY_HINT_INFO));
        }
        catch (ObjectDisposedException)
        {
            // Closed already: nothing to hint.
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }
}
