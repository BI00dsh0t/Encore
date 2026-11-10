using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Encore.Platform.Windows;

/// <summary>A visible, titled top-level window.</summary>
public sealed record WindowInfo(nint Handle, string Title, uint ProcessId, string? ProcessName);

public enum WindowMatchOutcome
{
    Found,
    NotFound,
    Ambiguous,
}

/// <summary>
/// The result of matching a title: the window when exactly one matched, and otherwise the
/// candidates to list (every window when none matched, the matches when several did).
/// </summary>
public sealed record WindowMatch(WindowMatchOutcome Outcome, WindowInfo? Window, IReadOnlyList<WindowInfo> Candidates);

/// <summary>
/// Lists top-level windows without touching their processes beyond
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> (AD-12).
/// </summary>
public static unsafe class WindowFinder
{
    /// <summary>Visible, titled, uncloaked top-level windows, in Z order.</summary>
    public static IReadOnlyList<WindowInfo> List()
    {
        var handles = new List<HWND>();
        var gc = GCHandle.Alloc(handles);
        try
        {
            PInvoke.EnumWindows(&Collect, new LPARAM(GCHandle.ToIntPtr(gc)));
        }
        finally
        {
            gc.Free();
        }

        var windows = new List<WindowInfo>();
        foreach (var hwnd in handles)
        {
            if (Describe(hwnd) is { } info)
            {
                windows.Add(info);
            }
        }

        return windows;
    }

    /// <summary>The window for a raw handle (<c>--hwnd</c>), or null when it isn't a visible titled window.</summary>
    public static WindowInfo? FromHandle(nint handle)
    {
        var hwnd = new HWND((void*)handle);
        return PInvoke.IsWindow(hwnd) ? Describe(hwnd) : null;
    }

    /// <summary>
    /// Why the window can't be captured yet, or null when it can: it must exist, be visible, not be
    /// minimized or cloaked, and have a client area of at least <paramref name="minWidth"/> ×
    /// <paramref name="minHeight"/>. A game still starting can show a tiny placeholder window that
    /// WGC never delivers a frame from.
    /// </summary>
    public static string? NotCapturable(nint handle, int minWidth, int minHeight)
    {
        var hwnd = new HWND((void*)handle);
        if (!PInvoke.IsWindow(hwnd))
        {
            return "the window is gone";
        }

        if (!PInvoke.IsWindowVisible(hwnd))
        {
            return "the window is hidden";
        }

        if (PInvoke.IsIconic(hwnd))
        {
            return "the window is minimized";
        }

        if (IsCloaked(hwnd))
        {
            return "the window is cloaked";
        }

        if (!PInvoke.GetClientRect(hwnd, out var client))
        {
            return "the window's size can't be read";
        }

        var width = client.right - client.left;
        var height = client.bottom - client.top;
        return width < minWidth || height < minHeight
            ? $"the window is {width}x{height}, under {minWidth}x{minHeight}"
            : null;
    }

    /// <summary>
    /// The window of process <paramref name="processId"/> to capture: <paramref name="preferred"/>
    /// when it is the process's, titled and <see cref="NotCapturable"/> accepts it; else the
    /// process's largest titled, non-tool top-level window that it accepts (a splash and a main
    /// window: the main one). Null, with why, when none can be captured.
    /// </summary>
    public static (nint Window, string? Why) CaptureWindowOf(int processId, nint preferred, int minWidth, int minHeight)
    {
        string? preferredWhy = null;
        if (preferred != 0 && FromHandle(preferred) is { } window && (int)window.ProcessId == processId)
        {
            preferredWhy = NotCapturable(preferred, minWidth, minHeight);
            if (preferredWhy is null)
            {
                return (preferred, null);
            }
        }

        var handles = new List<HWND>();
        var gc = GCHandle.Alloc(handles);
        try
        {
            PInvoke.EnumWindows(&Collect, new LPARAM(GCHandle.ToIntPtr(gc)));
        }
        finally
        {
            gc.Free();
        }

        nint best = 0;
        long bestArea = 0;
        foreach (var hwnd in handles)
        {
            uint pid;
            PInvoke.GetWindowThreadProcessId(hwnd, &pid);
            if ((int)pid != processId
                || PInvoke.GetWindowTextLength(hwnd) <= 0
                || ((WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0
                || NotCapturable((nint)hwnd.Value, minWidth, minHeight) is not null
                || !PInvoke.GetClientRect(hwnd, out var client))
            {
                continue;
            }

            var area = (long)(client.right - client.left) * (client.bottom - client.top);
            if (area > bestArea)
            {
                best = (nint)hwnd.Value;
                bestArea = area;
            }
        }

        return best != 0
            ? (best, null)
            : (0, preferredWhy ?? "no window of the process can be captured");
    }

    /// <summary>
    /// Matches <paramref name="query"/> against window titles: a single exact title
    /// (ignoring case) wins, otherwise the title must contain the query exactly once.
    /// </summary>
    public static WindowMatch Match(IReadOnlyList<WindowInfo> windows, string query)
    {
        var exact = windows.Where(w => string.Equals(w.Title, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return new WindowMatch(WindowMatchOutcome.Found, exact[0], exact);
        }

        var matches = windows.Where(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            0 => new WindowMatch(WindowMatchOutcome.NotFound, null, windows),
            1 => new WindowMatch(WindowMatchOutcome.Found, matches[0], matches),
            _ => new WindowMatch(WindowMatchOutcome.Ambiguous, null, matches),
        };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL Collect(HWND hwnd, LPARAM lParam)
    {
        var handles = (List<HWND>)GCHandle.FromIntPtr(lParam.Value).Target!;
        handles.Add(hwnd);
        return true;
    }

    private static WindowInfo? Describe(HWND hwnd)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || IsCloaked(hwnd))
        {
            return null;
        }

        var length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[length + 1];
        var copied = PInvoke.GetWindowText(hwnd, buffer);
        var title = new string(buffer[..copied]).Trim();
        if (title.Length == 0)
        {
            return null;
        }

        PInvoke.GetWindowThreadProcessId(hwnd, out var pid);
        return new WindowInfo((nint)hwnd.Value, title, pid, ProcessName(pid));
    }

    private static bool IsCloaked(HWND hwnd)
    {
        uint cloaked = 0;
        var hr = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
        return hr.Succeeded && cloaked != 0;
    }

    private static string? ProcessName(uint pid)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
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
