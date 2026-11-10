using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Encore.Platform.Windows;

public static unsafe partial class RecycleBin
{
    // PreDeleteItem's flag: the item goes to the Recycle Bin; without it, the delete would be permanent.
    private const uint TsfDeleteRecycleIfPossible = 0x80;
    private static readonly HRESULT Ok = new(0);
    private static readonly HRESULT NoInterface = new(unchecked((int)0x80004002));
    private static readonly HRESULT Aborted = new(unchecked((int)0x80004004));

    // The sink's vtable: IUnknown, then IFileOperationProgressSink's 16 methods in shobjidl order. Lives for the process.
    private static readonly void** SinkVtable = CreateSinkVtable();

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
    private static HRESULT PostMoveItem(
        Sink* sink, uint flags, void* item, void* destination, char* newName, HRESULT result, void* created) => Ok;

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
    private static HRESULT PostNewItem(
        Sink* sink, uint flags, void* destination, char* newName, char* template, uint attributes, HRESULT result, void* created) => Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT UpdateProgress(Sink* sink, uint total, uint soFar) => Ok;

    // A native IFileOperationProgressSink: its vtable, and whether it refused a permanent delete.
    private struct Sink
    {
        public void** Vtable;
        public int Refused;
    }
}
