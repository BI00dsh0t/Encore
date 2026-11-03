using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Threading;

namespace ReplayBuffer.Platform.Windows;

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
