using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Encore.Platform.Desktop;

/// <summary>Keeps this process's windows out of screen captures; opt-in, and the App does not turn it on.</summary>
public static unsafe class CaptureExclusion
{
    /// <summary>Excludes one top-level window of this process from capture; throws Win32Exception for any other window.</summary>
    public static void Apply(nint window)
    {
        if (!PInvoke.SetWindowDisplayAffinity(new HWND(window), WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
        {
            throw new Win32Exception();
        }
    }

    /// <summary>Whether the window is excluded from capture; throws Win32Exception when the window doesn't exist.</summary>
    public static bool IsApplied(nint window)
    {
        uint affinity;
        if (!PInvoke.GetWindowDisplayAffinity(new HWND(window), &affinity))
        {
            throw new Win32Exception();
        }

        return affinity == (uint)WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE;
    }

    /// <summary>Excludes each new top-level window of this process from capture; dispose the result on the same thread.</summary>
    public static IDisposable ExcludeNewWindows()
    {
        // The callback is a static [UnmanagedCallersOnly] method, so there is no delegate to keep alive.
        var hook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_OBJECT_CREATE,
            PInvoke.EVENT_OBJECT_SHOW,
            default(HMODULE),
            &OnWinEvent,
            (uint)Environment.ProcessId,
            0,
            PInvoke.WINEVENT_OUTOFCONTEXT);
        if (hook.IsNull)
        {
            throw new Win32Exception();
        }

        return new Hook(hook);
    }

    /// <summary>This process's visible top-level windows.</summary>
    public static IReadOnlyList<nint> VisibleWindows()
    {
        var windows = new List<nint>();
        var handle = GCHandle.Alloc(windows);
        try
        {
            PInvoke.EnumWindows(&OnEnumWindow, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return windows;
    }

    /// <summary>Whether a WinEvent names a top-level window, not a child or an accessible object inside a window.</summary>
    internal static bool NamesATopLevelWindow(uint @event, nint window, int idObject, int idChild) =>
        @event is PInvoke.EVENT_OBJECT_CREATE or PInvoke.EVENT_OBJECT_SHOW
        && idObject == 0 // OBJID_WINDOW
        && idChild == 0 // CHILDID_SELF
        && window != 0
        && PInvoke.GetAncestor(new HWND(window), GET_ANCESTOR_FLAGS.GA_ROOT) == new HWND(window);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(
        HWINEVENTHOOK hook, uint @event, HWND window, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (NamesATopLevelWindow(@event, window, idObject, idChild))
            {
                // A message-only window, or one already gone, refuses; skip it.
                PInvoke.SetWindowDisplayAffinity(window, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);
            }
        }
        catch
        {
            // Never let an exception unwind into user32.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnEnumWindow(HWND window, LPARAM lParam)
    {
        try
        {
            uint owner;
            if (PInvoke.GetWindowThreadProcessId(window, &owner) != 0
                && owner == (uint)Environment.ProcessId
                && PInvoke.IsWindowVisible(window))
            {
                ((List<nint>)GCHandle.FromIntPtr(lParam).Target!).Add(window);
            }

            return true;
        }
        catch
        {
            // Never let an exception unwind into user32; stop the enumeration instead.
            return false;
        }
    }

    private sealed class Hook(HWINEVENTHOOK hook) : IDisposable
    {
        private HWINEVENTHOOK _hook = hook;

        public void Dispose()
        {
            if (!_hook.IsNull)
            {
                PInvoke.UnhookWinEvent(_hook);
                _hook = default;
            }
        }
    }
}
