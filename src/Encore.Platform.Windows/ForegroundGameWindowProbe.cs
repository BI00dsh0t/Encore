using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Encore.Platform.Windows;

/// <summary>The real probe: reads only window geometry, style and owner, and opens processes with limited access. No hooks.</summary>
public sealed unsafe class ForegroundGameWindowProbe : IGameWindowProbe
{
    private const uint StillActive = 259;

    public GameWindow? ForegroundGameWindow()
    {
        var window = PInvoke.GetForegroundWindow();
        if (window.IsNull || !CoversItsMonitorWithoutCaption((nint)window.Value))
        {
            return null;
        }

        // Explorer's full-screen surfaces (Task View, the Alt+Tab host) are caption-less and cover the monitor.
        return NotTheShells(window, ShellProcessId());
    }

    public GameWindow? ForegroundWindow()
    {
        var window = PInvoke.GetForegroundWindow();
        return window.IsNull ? null : NotTheShells(window, ShellProcessId());
    }

    public IReadOnlyList<GameWindow> Windows()
    {
        var handles = TopLevelWindows();
        var shellProcessId = ShellProcessId();
        var windows = new List<GameWindow>();
        foreach (var window in handles)
        {
            if (IsListedWindow(window) && NotTheShells(window, shellProcessId) is { } listed)
            {
                windows.Add(listed);
            }
        }

        return windows;
    }

    public IGameProcess? Open(int processId)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsNull)
        {
            return null;
        }

        const int capacity = 1024;
        var buffer = stackalloc char[capacity];
        var size = (uint)capacity;
        if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR(buffer), &size))
        {
            PInvoke.CloseHandle(process);
            return null;
        }

        return new LimitedProcess(process, new string(buffer, 0, (int)size), StartTimeOf(process));
    }

    /// <summary>Whether a window is borderless or fullscreen: visible, no caption, not the shell's, and covering its monitor.</summary>
    public static bool CoversItsMonitorWithoutCaption(nint handle)
    {
        var window = new HWND((void*)handle);
        if (!PInvoke.IsWindow(window)
            || window == PInvoke.GetShellWindow()
            || !PInvoke.IsWindowVisible(window)
            || PInvoke.IsIconic(window)
            || IsCloaked(window)
            || IsDesktopClass(window))
        {
            return false;
        }

        var style = (WINDOW_STYLE)(uint)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        if ((style & WINDOW_STYLE.WS_CAPTION) == WINDOW_STYLE.WS_CAPTION)
        {
            return false;
        }

        RECT rect;
        if (!PInvoke.GetWindowRect(window, &rect))
        {
            return false;
        }

        var monitor = PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, &info))
        {
            return false;
        }

        var bounds = info.rcMonitor;
        return rect.left <= bounds.left && rect.top <= bounds.top && rect.right >= bounds.right && rect.bottom >= bounds.bottom;
    }

    // Read once, on the limited handle.
    private static DateTime StartTimeOf(HANDLE process)
    {
        System.Runtime.InteropServices.ComTypes.FILETIME created, exited, kernel, user;
        if (!PInvoke.GetProcessTimes(process, &created, &exited, &kernel, &user))
        {
            return DateTime.MinValue;
        }

        var ticks = ((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        return DateTime.FromFileTimeUtc(ticks);
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

    // Visible, uncloaked, unowned and not a tool window.
    private static bool IsListedWindow(HWND window)
    {
        if (!PInvoke.IsWindowVisible(window)
            || IsCloaked(window)
            || !PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_OWNER).IsNull)
        {
            return false;
        }

        var extendedStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        return (extendedStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) == 0;
    }

    private static uint ShellProcessId()
    {
        uint shellProcessId = 0;
        var shell = PInvoke.GetShellWindow();
        if (!shell.IsNull)
        {
            PInvoke.GetWindowThreadProcessId(shell, &shellProcessId);
        }

        return shellProcessId;
    }

    private static GameWindow? NotTheShells(HWND window, uint shellProcessId)
    {
        uint processId;
        PInvoke.GetWindowThreadProcessId(window, &processId);
        return processId == 0 || processId == shellProcessId ? null : new GameWindow((nint)window.Value, (int)processId);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL Collect(HWND window, LPARAM data)
    {
        var handles = (List<HWND>)GCHandle.FromIntPtr(data.Value).Target!;
        handles.Add(window);
        return true;
    }

    private static bool IsCloaked(HWND window)
    {
        uint cloaked = 0;
        var result = PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
        return result.Succeeded && cloaked != 0;
    }

    private static bool IsDesktopClass(HWND window)
    {
        const int capacity = 64;
        var buffer = stackalloc char[capacity];
        var length = PInvoke.GetClassName(window, new PWSTR(buffer), capacity);
        var name = new ReadOnlySpan<char>(buffer, Math.Max(length, 0));
        return name.SequenceEqual("Progman") || name.SequenceEqual("WorkerW");
    }

    private sealed class LimitedProcess(HANDLE handle, string imagePath, DateTime startTime) : IGameProcess
    {
        private int _disposed;

        public string ImagePath { get; } = imagePath;

        public DateTime StartTime { get; } = startTime;

        // Polled, because the limited handle has no SYNCHRONIZE right to wait on.
        public bool HasExited
        {
            get
            {
                uint code;
                return PInvoke.GetExitCodeProcess(handle, &code) && code != StillActive;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                PInvoke.CloseHandle(handle);
            }
        }
    }
}
