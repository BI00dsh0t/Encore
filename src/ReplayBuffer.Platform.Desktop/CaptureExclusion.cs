using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ReplayBuffer.Platform.Desktop;

/// <summary>
/// Keeps the App's windows out of every capture, its own included (AD-15):
/// <c>WDA_EXCLUDEFROMCAPTURE</c> on a top-level window of this process.
/// </summary>
public static unsafe class CaptureExclusion
{
    /// <exception cref="Win32Exception">The window isn't a top-level window of this process.</exception>
    public static void Apply(nint hwnd)
    {
        if (!PInvoke.SetWindowDisplayAffinity(new HWND(hwnd), WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
        {
            throw new Win32Exception();
        }
    }

    /// <summary>Whether the window is excluded from capture.</summary>
    /// <exception cref="Win32Exception">The window doesn't exist.</exception>
    public static bool IsApplied(nint hwnd)
    {
        uint affinity;
        if (!PInvoke.GetWindowDisplayAffinity(new HWND(hwnd), &affinity))
        {
            throw new Win32Exception();
        }

        return affinity == (uint)WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE;
    }

    /// <summary>
    /// Excludes every top-level window of this process from capture as it is created or shown: WPF
    /// windows, popups and native dialogs alike. The hook is out of context, so its events arrive while
    /// the calling thread pumps messages, as the WPF UI thread does; a window may show for a moment
    /// before it is excluded. Dispose the result on the same thread to unhook.
    /// </summary>
    /// <exception cref="Win32Exception">The hook couldn't be installed.</exception>
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

    /// <summary>
    /// Whether a WinEvent names a top-level window being created or shown: not a child, and not an
    /// accessible object inside a window.
    /// </summary>
    internal static bool NamesATopLevelWindow(uint @event, nint hwnd, int idObject, int idChild) =>
        @event is PInvoke.EVENT_OBJECT_CREATE or PInvoke.EVENT_OBJECT_SHOW
        && idObject == 0 // OBJID_WINDOW
        && idChild == 0 // CHILDID_SELF
        && hwnd != 0
        && PInvoke.GetAncestor(new HWND(hwnd), GET_ANCESTOR_FLAGS.GA_ROOT) == new HWND(hwnd);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(
        HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (NamesATopLevelWindow(@event, hwnd, idObject, idChild))
            {
                // A message-only window, or one already gone, refuses; skip it.
                PInvoke.SetWindowDisplayAffinity(hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);
            }
        }
        catch
        {
            // Never let an exception unwind into user32.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnEnumWindow(HWND hwnd, LPARAM lParam)
    {
        try
        {
            uint owner;
            if (PInvoke.GetWindowThreadProcessId(hwnd, &owner) != 0
                && owner == (uint)Environment.ProcessId
                && PInvoke.IsWindowVisible(hwnd))
            {
                ((List<nint>)GCHandle.FromIntPtr(lParam).Target!).Add(hwnd);
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
