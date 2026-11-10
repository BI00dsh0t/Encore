using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Encore.Platform.Windows;

/// <summary>Moves a file to the Recycle Bin through the shell; it never deletes permanently and never shows UI.</summary>
public static unsafe class RecycleBin
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);

    private const FILEOPERATION_FLAGS Flags =
        FILEOPERATION_FLAGS.FOF_ALLOWUNDO
        | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION
        | FILEOPERATION_FLAGS.FOF_SILENT
        | FILEOPERATION_FLAGS.FOF_NOERRORUI
        | FILEOPERATION_FLAGS.FOFX_EARLYFAILURE
        | FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE;

    // PreDeleteItem's flag: the item goes to the Recycle Bin; without it, the delete would be permanent.
    private const uint TsfDeleteRecycleIfPossible = 0x80;
    private static readonly HRESULT Ok = new(0);
    private static readonly HRESULT NoInterface = new(unchecked((int)0x80004002));
    private static readonly HRESULT Aborted = new(unchecked((int)0x80004004));

    // The sink's vtable: IUnknown, then IFileOperationProgressSink's 16 methods in shobjidl order. Lives for the process.
    private static readonly void** SinkVtable = CreateSinkVtable();

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
            var sink = new Sink { Vtable = SinkVtable };
            var cookie = 0u;
            var advised = false;
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

    // A sharing violation from the copy engine reads as Win32's, so a file held open is retried like a rename.
    private static int Win32Code(int hresult) =>
        hresult is unchecked((int)0x80270027) or unchecked((int)0x80270028) ? ErrorSharingViolation : hresult;

    private static void** CreateSinkVtable()
    {
        var vtable = (void**)NativeMemory.Alloc(19, (nuint)sizeof(void*));
        vtable[0] = (delegate* unmanaged[Stdcall]<Sink*, Guid*, void**, HRESULT>)&QueryInterface;
        vtable[1] = (delegate* unmanaged[Stdcall]<Sink*, uint>)&AddRef;
        vtable[2] = (delegate* unmanaged[Stdcall]<Sink*, uint>)&Release;
        vtable[3] = (delegate* unmanaged[Stdcall]<Sink*, HRESULT>)&NoArguments;
        vtable[4] = (delegate* unmanaged[Stdcall]<Sink*, HRESULT, HRESULT>)&FinishOperations;
        vtable[5] = (delegate* unmanaged[Stdcall]<Sink*, uint, void*, char*, HRESULT>)&PreRenameItem;
        vtable[6] = (delegate* unmanaged[Stdcall]<Sink*, uint, void*, char*, HRESULT, void*, HRESULT>)&PostRenameItem;
        vtable[7] = (delegate* unmanaged[Stdcall]<Sink*, uint, void*, void*, char*, HRESULT>)&PreMoveItem;
        vtable[8] = (delegate* unmanaged[Stdcall]<Sink*, uint, void*, void*, char*, HRESULT, void*, HRESULT>)&PostMoveItem;
        vtable[9] = (delegate* unmanaged[Stdcall]<Sink*, uint, void*, void*, char*, HRESULT>)&PreMoveItem;
        vtable[10] = (delegate* unmanaged[Stdcall]<Sink*, uint, void*, void*, char*, HRESULT, void*, HRESULT>)&PostMoveItem;
        vtable[11] = (delegate* unmanaged[Stdcall]<Sink*, uint, void*, HRESULT>)&PreDeleteItem;
        vtable[12] = (delegate* unmanaged[Stdcall]<Sink*, uint, void*, HRESULT, void*, HRESULT>)&PostDeleteItem;
        vtable[13] = (delegate* unmanaged[Stdcall]<Sink*, uint, void*, char*, HRESULT>)&PreRenameItem;
        vtable[14] = (delegate* unmanaged[Stdcall]<Sink*, uint, void*, char*, char*, uint, HRESULT, void*, HRESULT>)&PostNewItem;
        vtable[15] = (delegate* unmanaged[Stdcall]<Sink*, uint, uint, HRESULT>)&UpdateProgress;
        vtable[16] = (delegate* unmanaged[Stdcall]<Sink*, HRESULT>)&NoArguments;
        vtable[17] = (delegate* unmanaged[Stdcall]<Sink*, HRESULT>)&NoArguments;
        vtable[18] = (delegate* unmanaged[Stdcall]<Sink*, HRESULT>)&NoArguments;
        return vtable;
    }

    // IUnknown and IFileOperationProgressSink only; the sink lives on the STA thread's stack for the call.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT QueryInterface(Sink* sink, Guid* iid, void** result)
    {
        if (*iid == IUnknown.IID_Guid || *iid == IFileOperationProgressSink.IID_Guid)
        {
            *result = sink;
            return Ok;
        }

        *result = null;
        return NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(Sink* sink) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(Sink* sink) => 1;

    // StartOperations, ResetTimer, PauseTimer and ResumeTimer.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT NoArguments(Sink* sink) => Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT FinishOperations(Sink* sink, HRESULT result) => Ok;

    // Also PreNewItem, whose arguments have the same shape.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT PreRenameItem(Sink* sink, uint flags, void* item, char* newName) => Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT PostRenameItem(Sink* sink, uint flags, void* item, char* newName, HRESULT result, void* created) => Ok;

    // Also PreCopyItem.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT PreMoveItem(Sink* sink, uint flags, void* item, void* destination, char* newName) => Ok;

    // Also PostCopyItem.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT PostMoveItem(Sink* sink, uint flags, void* item, void* destination, char* newName, HRESULT result, void* created) => Ok;

    // A delete the shell can't recycle would be permanent: refused, so nothing is deleted.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT PreDeleteItem(Sink* sink, uint flags, void* item)
    {
        if ((flags & TsfDeleteRecycleIfPossible) != 0)
        {
            return Ok;
        }

        sink->Refused = 1;
        return Aborted;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT PostDeleteItem(Sink* sink, uint flags, void* item, HRESULT result, void* created) => Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT PostNewItem(Sink* sink, uint flags, void* destination, char* newName, char* template, uint attributes, HRESULT result, void* created) => Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT UpdateProgress(Sink* sink, uint total, uint soFar) => Ok;

    private static void Check(HRESULT result)
    {
        if (result.Failed)
        {
            throw new IOException("The file couldn't be moved to the Recycle Bin.", Win32Code(result.Value));
        }
    }

    // A native IFileOperationProgressSink: its vtable, and whether it refused a permanent delete.
    private struct Sink
    {
        public void** Vtable;
        public int Refused;
    }
}
