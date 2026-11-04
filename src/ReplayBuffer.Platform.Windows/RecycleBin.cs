using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace ReplayBuffer.Platform.Windows;

/// <summary>
/// Moves a file to the Recycle Bin through the shell's <c>IFileOperation</c> (story 7.7), the Library's
/// delete. It never deletes permanently: a volume that isn't a fixed drive with a Recycle Bin is refused
/// up front, and the shell is asked to warn (rather than nuke) when it couldn't recycle.
/// </summary>
public static unsafe class RecycleBin
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);

    private const FILEOPERATION_FLAGS Flags =
        FILEOPERATION_FLAGS.FOF_ALLOWUNDO
        | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION
        | FILEOPERATION_FLAGS.FOF_SILENT
        | FILEOPERATION_FLAGS.FOF_NOERRORUI
        | FILEOPERATION_FLAGS.FOFX_EARLYFAILURE
        | FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE
        | FILEOPERATION_FLAGS.FOF_WANTNUKEWARNING;

    /// <summary>
    /// Moves <paramref name="path"/> (absolute) to the Recycle Bin, on a new STA thread. False when its
    /// volume has no Recycle Bin, or the operation was aborted, so the file wasn't deleted; throws an
    /// <see cref="IOException"/> whose <see cref="Exception.HResult"/> is the shell's when it failed.
    /// </summary>
    public static bool Recycle(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        var recycled = false;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                recycled = RecycleOnSta(full);
            }
#pragma warning disable CA1031 // Rethrown on the caller's thread.
            catch (Exception e)
#pragma warning restore CA1031
            {
                error = e;
            }
        })
        {
            Name = "RecycleBin",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error is IOException ? error : new IOException($"The file couldn't be moved to the Recycle Bin: {error.Message}", error.HResult);
        }

        return recycled;
    }

    private static bool RecycleOnSta(string path)
    {
        var init = PInvoke.CoInitializeEx(COINIT.COINIT_APARTMENTTHREADED | COINIT.COINIT_DISABLE_OLE1DDE);
        if (init.Failed)
        {
            throw new IOException("COM couldn't be initialized for the Recycle Bin.", init.Value);
        }

        try
        {
            if (!HasRecycleBin(path))
            {
                return false;
            }

            IFileOperation* operation = null;
            IShellItem* item = null;
            try
            {
                Check(FileOperation.CreateInstance(out operation));
                var iid = IShellItem.IID_Guid;
                void* created;
                fixed (char* name = path)
                {
                    Check(PInvoke.SHCreateItemFromParsingName(name, null, &iid, &created));
                }

                item = (IShellItem*)created;
                operation->SetOperationFlags(Flags);
                operation->DeleteItem(item, null);
                operation->PerformOperations();
                BOOL aborted;
                operation->GetAnyOperationsAborted(&aborted);
                return !aborted;
            }
            catch (COMException e)
            {
                throw new IOException($"The file couldn't be moved to the Recycle Bin: {e.Message}", Win32Code(e.HResult));
            }
            finally
            {
                if (item is not null)
                {
                    item->Release();
                }

                if (operation is not null)
                {
                    operation->Release();
                }
            }
        }
        finally
        {
            PInvoke.CoUninitialize();
        }
    }

    // Only a fixed drive whose Recycle Bin answers; anything else would be deleted permanently.
    private static bool HasRecycleBin(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || PInvoke.GetDriveType(root) != PInvoke.DRIVE_FIXED)
        {
            return false;
        }

        var info = new SHQUERYRBINFO { cbSize = (uint)sizeof(SHQUERYRBINFO) };
        return PInvoke.SHQueryRecycleBin(root, ref info).Succeeded;
    }

    // The copy engine's sharing violations (COPYENGINE_E_SHARING_VIOLATION_SRC/_DEST) read as Win32's,
    // so a file another process holds open (the length read) is tried again like a rename.
    private static int Win32Code(int hresult) =>
        hresult is unchecked((int)0x80270027) or unchecked((int)0x80270028) ? ErrorSharingViolation : hresult;

    private static void Check(HRESULT result)
    {
        if (result.Failed)
        {
            throw new IOException("The file couldn't be moved to the Recycle Bin.", Win32Code(result.Value));
        }
    }
}
