using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Encore.Platform.Windows;

/// <summary>Lists top-level windows, opening their processes with limited access only.</summary>
public static unsafe class WindowFinder
{
    /// <summary>Visible, titled, uncloaked top-level windows, in Z order.</summary>
    public static IReadOnlyList<WindowInfo> List()
    {
        var windows = new List<WindowInfo>();
        foreach (var window in TopLevelWindows())
        {
            if (Describe(window) is { } info)
            {
                windows.Add(info);
            }
        }

        return windows;
    }

    /// <summary>The window for a raw handle (<c>--hwnd</c>), or null when it isn't a visible titled window.</summary>
    public static WindowInfo? FromHandle(nint handle)
    {
        var window = new HWND((void*)handle);
        return PInvoke.IsWindow(window) ? Describe(window) : null;
    }

    /// <summary>Why the window can't be captured yet, or null when it can; a game still starting may show a tiny placeholder.</summary>
    public static string? NotCapturable(nint handle, int minWidth, int minHeight)
    {
        var window = new HWND((void*)handle);
        if (!PInvoke.IsWindow(window))
        {
            return "the window is gone";
        }

        if (!PInvoke.IsWindowVisible(window))
        {
            return "the window is hidden";
        }

        if (PInvoke.IsIconic(window))
        {
            return "the window is minimized";
        }

        if (IsCloaked(window))
        {
            return "the window is cloaked";
        }

        if (!PInvoke.GetClientRect(window, out var client))
        {
            return "the window's size can't be read";
        }

        var width = client.right - client.left;
        var height = client.bottom - client.top;
        return width < minWidth || height < minHeight
            ? $"the window is {width}x{height}, under {minWidth}x{minHeight}"
            : null;
    }

    /// <summary>The process's window to capture: the preferred one when usable, else its largest titled one; null with why.</summary>
    public static (nint Window, string? Why) CaptureWindowOf(int processId, nint preferred, int minWidth, int minHeight)
    {
        string? preferredWhy = null;
        if (preferred != 0 && FromHandle(preferred) is { } preferredWindow && (int)preferredWindow.ProcessId == processId)
        {
            preferredWhy = NotCapturable(preferred, minWidth, minHeight);
            if (preferredWhy is null)
            {
                return (preferred, null);
            }
        }

        nint best = 0;
        long bestArea = 0;
        foreach (var window in TopLevelWindows())
        {
            if (CapturableArea(window, processId, minWidth, minHeight) is { } area && area > bestArea)
            {
                best = (nint)window.Value;
                bestArea = area;
            }
        }

        return best != 0
            ? (best, null)
            : (0, preferredWhy ?? "no window of the process can be captured");
    }

    /// <summary>Matches a query against window titles: one exact title wins, otherwise exactly one title must contain it.</summary>
    public static WindowMatch Match(IReadOnlyList<WindowInfo> windows, string query)
    {
        var exact = windows.Where(window => string.Equals(window.Title, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return new WindowMatch(WindowMatchOutcome.Found, exact[0], exact);
        }

        var matches = windows.Where(window => window.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            0 => new WindowMatch(WindowMatchOutcome.NotFound, null, windows),
            1 => new WindowMatch(WindowMatchOutcome.Found, matches[0], matches),
            _ => new WindowMatch(WindowMatchOutcome.Ambiguous, null, matches),
        };
    }

    private static List<HWND> TopLevelWindows()
    {
        var handles = new List<HWND>();
        var handle = GCHandle.Alloc(handles);
        try
        {
            PInvoke.EnumWindows(&Collect, new LPARAM(GCHandle.ToIntPtr(handle)));
        }
        finally
        {
            handle.Free();
        }

        return handles;
    }

    // The client area of the process's titled, non-tool window when it can be captured; null otherwise.
    private static long? CapturableArea(HWND window, int processId, int minWidth, int minHeight)
    {
        uint owner;
        PInvoke.GetWindowThreadProcessId(window, &owner);
        if ((int)owner != processId
            || PInvoke.GetWindowTextLength(window) <= 0
            || IsToolWindow(window)
            || NotCapturable((nint)window.Value, minWidth, minHeight) is not null
            || !PInvoke.GetClientRect(window, out var client))
        {
            return null;
        }

        return (long)(client.right - client.left) * (client.bottom - client.top);
    }

    private static bool IsToolWindow(HWND window) =>
        ((WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL Collect(HWND window, LPARAM data)
    {
        var handles = (List<HWND>)GCHandle.FromIntPtr(data.Value).Target!;
        handles.Add(window);
        return true;
    }

    private static WindowInfo? Describe(HWND window)
    {
        if (!PInvoke.IsWindowVisible(window) || IsCloaked(window))
        {
            return null;
        }

        var length = PInvoke.GetWindowTextLength(window);
        if (length <= 0)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[length + 1];
        var copied = PInvoke.GetWindowText(window, buffer);
        var title = new string(buffer[..copied]).Trim();
        if (title.Length == 0)
        {
            return null;
        }

        PInvoke.GetWindowThreadProcessId(window, out var processId);
        return new WindowInfo((nint)window.Value, title, processId, ProcessName(processId));
    }

    private static bool IsCloaked(HWND window)
    {
        uint cloaked = 0;
        var result = PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
        return result.Succeeded && cloaked != 0;
    }

    private static string? ProcessName(uint processId)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsNull)
        {
            return null;
        }

        try
        {
            const int capacity = 1024;
            var buffer = stackalloc char[capacity];
            var size = (uint)capacity;
            return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR(buffer), &size)
                ? Path.GetFileName(new string(buffer, 0, (int)size))
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }
}
