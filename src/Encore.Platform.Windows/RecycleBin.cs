using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Encore.Platform.Windows;

/// <summary>Moves a file to the Recycle Bin through the shell; it never deletes permanently and never shows UI.</summary>
public static unsafe partial class RecycleBin
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);

    private const FILEOPERATION_FLAGS Flags =
        FILEOPERATION_FLAGS.FOF_ALLOWUNDO
        | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION
        | FILEOPERATION_FLAGS.FOF_SILENT
        | FILEOPERATION_FLAGS.FOF_NOERRORUI
        | FILEOPERATION_FLAGS.FOFX_EARLYFAILURE
        | FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE;

    /// <summary>Recycles a file on a new STA thread. False when it wasn't deleted; throws IOException when the shell failed.</summary>
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
            throw error is IOException
                ? error
                : new IOException($"The file couldn't be moved to the Recycle Bin: {error.Message}", error.HResult);
        }

        return recycled;
    }

    private static bool RecycleOnSta(string path)
    {
        var initialized = PInvoke.CoInitializeEx(COINIT.COINIT_APARTMENTTHREADED | COINIT.COINIT_DISABLE_OLE1DDE);
        if (initialized.Failed)
        {
            throw new IOException("COM couldn't be initialized for the Recycle Bin.", initialized.Value);
        }

        try
        {
            return HasRecycleBin(path) && DeleteThroughShell(path);
        }
        finally
        {
            PInvoke.CoUninitialize();
        }
    }

    /// <summary>One shell delete of the file with the sink watching; false when the sink refused it or the shell aborted.</summary>
    private static bool DeleteThroughShell(string path)
    {
        IFileOperation* operation = null;
        IShellItem* item = null;
        var sink = new Sink { Vtable = SinkVtable };
        var cookie = 0u;
        var advised = false;
        try
        {
            Check(FileOperation.CreateInstance(out operation));
            item = CreateItem(path);
            operation->SetOperationFlags(Flags);
            operation->DeleteItem(item, null);
            operation->Advise((IFileOperationProgressSink*)&sink, &cookie);
            advised = true;
            operation->PerformOperations();
            BOOL aborted;
            operation->GetAnyOperationsAborted(&aborted);
            return !aborted && sink.Refused == 0;
        }
#pragma warning disable CA1031 // A delete the sink refused failed because of it: no Recycle Bin.
        catch (Exception) when (sink.Refused != 0)
#pragma warning restore CA1031
        {
            return false;
        }
        catch (COMException e)
        {
            throw new IOException($"The file couldn't be moved to the Recycle Bin: {e.Message}", Win32Code(e.HResult));
        }
        finally
        {
            if (advised)
            {
                operation->Unadvise(cookie);
            }

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

    /// <summary>The shell item for a path, which the caller releases.</summary>
    private static IShellItem* CreateItem(string path)
    {
        var iid = IShellItem.IID_Guid;
        void* created;
        fixed (char* name = path)
        {
            Check(PInvoke.SHCreateItemFromParsingName(name, null, &iid, &created));
        }

        return (IShellItem*)created;
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

    // A sharing violation from the copy engine reads as Win32's, so a file held open is retried like a rename.
    private static int Win32Code(int result) =>
        result is unchecked((int)0x80270027) or unchecked((int)0x80270028) ? ErrorSharingViolation : result;

    private static void Check(HRESULT result)
    {
        if (result.Failed)
        {
            throw new IOException("The file couldn't be moved to the Recycle Bin.", Win32Code(result.Value));
        }
    }
}
