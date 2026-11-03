using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Sessions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ReplayBuffer.Platform.Windows;

/// <summary>A window that may be a game's, and the process that owns it.</summary>
public readonly record struct GameWindow(nint Handle, int ProcessId);

/// <summary>A game's process, opened with <c>PROCESS_QUERY_LIMITED_INFORMATION</c> only (AD-12).</summary>
public interface IGameProcess : IDisposable
{
    /// <summary>The process's executable, as the system reports it (not canonical).</summary>
    string ImagePath { get; }

    /// <summary>Whether the process has exited.</summary>
    bool HasExited { get; }
}

/// <summary>What Game Detection reads from the system; tests fake it.</summary>
public interface IGameWindowProbe
{
    /// <summary>The foreground window when it covers its monitor with no caption and isn't the shell's; otherwise <c>null</c>.</summary>
    GameWindow? ForegroundGameWindow();

    /// <summary>Opens <paramref name="processId"/> with limited access, or returns <c>null</c> when it can't be opened or its image read.</summary>
    IGameProcess? Open(int processId);
}

/// <summary>
/// The real probe (AD-12): reads only the foreground window's geometry and style, and opens its
/// process with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>; no memory reads, module lists, hooks or WinEvents.
/// </summary>
public sealed unsafe class ForegroundGameWindowProbe : IGameWindowProbe
{
    private const uint StillActive = 259;

    public GameWindow? ForegroundGameWindow()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        if (hwnd.IsNull || !CoversItsMonitorWithoutCaption((nint)hwnd.Value))
        {
            return null;
        }

        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);

        // Explorer's full-screen surfaces (Task View, the Alt+Tab host) are caption-less and cover the monitor.
        uint shellPid = 0;
        var shell = PInvoke.GetShellWindow();
        if (!shell.IsNull)
        {
            PInvoke.GetWindowThreadProcessId(shell, &shellPid);
        }

        return pid == 0 || pid == shellPid ? null : new GameWindow((nint)hwnd.Value, (int)pid);
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

        return new LimitedProcess(process, new string(buffer, 0, (int)size));
    }

    /// <summary>
    /// Whether <paramref name="window"/> is borderless or fullscreen: visible, not cloaked or
    /// minimized, without <c>WS_CAPTION</c>, not the shell's desktop, and its rectangle contains its
    /// monitor's.
    /// </summary>
    public static bool CoversItsMonitorWithoutCaption(nint window)
    {
        var hwnd = new HWND((void*)window);
        if (!PInvoke.IsWindow(hwnd)
            || hwnd == PInvoke.GetShellWindow()
            || !PInvoke.IsWindowVisible(hwnd)
            || PInvoke.IsIconic(hwnd)
            || IsCloaked(hwnd)
            || IsDesktopClass(hwnd))
        {
            return false;
        }

        var style = (WINDOW_STYLE)(uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        if ((style & WINDOW_STYLE.WS_CAPTION) == WINDOW_STYLE.WS_CAPTION)
        {
            return false;
        }

        RECT rect;
        if (!PInvoke.GetWindowRect(hwnd, &rect))
        {
            return false;
        }

        var monitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, &info))
        {
            return false;
        }

        var bounds = info.rcMonitor;
        return rect.left <= bounds.left && rect.top <= bounds.top && rect.right >= bounds.right && rect.bottom >= bounds.bottom;
    }

    private static bool IsCloaked(HWND hwnd)
    {
        uint cloaked = 0;
        var hr = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
        return hr.Succeeded && cloaked != 0;
    }

    private static bool IsDesktopClass(HWND hwnd)
    {
        const int capacity = 64;
        var buffer = stackalloc char[capacity];
        var length = PInvoke.GetClassName(hwnd, new PWSTR(buffer), capacity);
        var name = new ReadOnlySpan<char>(buffer, Math.Max(length, 0));
        return name.SequenceEqual("Progman") || name.SequenceEqual("WorkerW");
    }

    private sealed class LimitedProcess(HANDLE handle, string imagePath) : IGameProcess
    {
        private int _disposed;

        public string ImagePath { get; } = imagePath;

        // AD-12: polled on the limited handle; no SYNCHRONIZE, so no wait.
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

/// <summary>
/// Game Detection (story 3.1 tracer): polls the foreground window; when it covers its monitor with
/// no caption and its process isn't excluded, canonicalizes the executable (AD-18) and proposes it
/// as the Target Game. It only proposes (AD-19). A detected process stays known, its limited handle
/// held, until it exits; the last one proposed exiting proposes no Target, reason exit.
/// </summary>
public sealed class GameMonitor : IDisposable
{
    private readonly IGameWindowProbe _probe;
    private readonly Func<int, bool> _isExcluded;
    private readonly Action<TargetCandidate?, TargetChangeReason> _propose;
    private readonly TimeSpan _interval;
    private readonly ILogger _log;
    private readonly ManualResetEventSlim _stop = new();

    // Poll's thread only.
    private readonly Dictionary<int, Known> _known = [];
    private int? _proposed;

    private Thread? _thread;
    private int _disposed;

    /// <param name="probe">Reads the foreground window and opens processes.</param>
    /// <param name="isExcluded">True for a process that is never a game: the engine's and the attached App's.</param>
    /// <param name="propose">Receives each proposal, on the monitor's thread.</param>
    /// <param name="interval">The time between polls.</param>
    public GameMonitor(
        IGameWindowProbe probe,
        Func<int, bool> isExcluded,
        Action<TargetCandidate?, TargetChangeReason> propose,
        TimeSpan interval,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(isExcluded);
        ArgumentNullException.ThrowIfNull(propose);
        ArgumentNullException.ThrowIfNull(log);
        _probe = probe;
        _isExcluded = isExcluded;
        _propose = propose;
        _interval = interval;
        _log = log;
    }

    /// <summary>Starts polling on a background thread, the first poll at once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run) { Name = "GameMonitor", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Stops polling, joins the thread and closes the process handles.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Set();
        if (_thread is not null && Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
        {
            _thread.Join();
        }

        foreach (var known in _known.Values)
        {
            known.Process.Dispose();
        }

        _known.Clear();
        _stop.Dispose();
    }

    /// <summary>One poll: forgets exited processes, then looks at the foreground window.</summary>
    internal void Poll()
    {
        ForgetExited();

        if (_probe.ForegroundGameWindow() is not { } window || _isExcluded(window.ProcessId))
        {
            return;
        }

        if (_known.TryGetValue(window.ProcessId, out var known))
        {
            // The Target with a new window: proposed again, so the capture can follow it. With no
            // Target (the last one exited), a known game in front is proposed. Another known game
            // never takes over an existing Target here; switching on focus is story 3.3's rule.
            var moved = known.Candidate.Window != window.Handle;
            known.Candidate = known.Candidate with { Window = window.Handle };
            if (_proposed is null || (moved && _proposed == window.ProcessId))
            {
                Propose(known.Candidate);
            }

            return;
        }

        var process = _probe.Open(window.ProcessId);
        if (process is null)
        {
            _log.LogDebug("Process {Pid} of a fullscreen window couldn't be opened; retrying next poll", window.ProcessId);
            return;
        }

        if (process.HasExited)
        {
            // Its window outlived it for a moment.
            process.Dispose();
            return;
        }

        string profileId;
        try
        {
            profileId = GamePaths.Canonicalize(process.ImagePath);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            process.Dispose();
            _log.LogDebug(e, "The executable of process {Pid} couldn't be canonicalized; retrying next poll", window.ProcessId);
            return;
        }

        var candidate = new TargetCandidate(window.ProcessId, window.Handle, profileId, Path.GetFileName(process.ImagePath));
        _known.Add(window.ProcessId, new Known(process, candidate));
        Propose(candidate);
    }

    private void ForgetExited()
    {
        List<int>? exited = null;
        foreach (var (pid, known) in _known)
        {
            if (known.Process.HasExited)
            {
                (exited ??= []).Add(pid);
            }
        }

        if (exited is null)
        {
            return;
        }

        foreach (var pid in exited)
        {
            _known.Remove(pid, out var known);
            known!.Process.Dispose();
            if (_proposed == pid)
            {
                _proposed = null;
                _log.LogInformation("The Target Game's process {Pid} exited", pid);
                _propose(null, TargetChangeReason.Exit);
            }
        }
    }

    private void Propose(TargetCandidate candidate)
    {
        _proposed = candidate.ProcessId;
        _log.LogInformation("Detected {Name} (process {Pid}, window 0x{Window:X}) at {ProfileId}", candidate.Name, candidate.ProcessId, candidate.Window, candidate.ProfileId);
        _propose(candidate, TargetChangeReason.Detected);
    }

    private void Run()
    {
        do
        {
            try
            {
                Poll();
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "A game detection poll failed");
            }
        }
        while (!_stop.Wait(_interval));
    }

    private sealed class Known(IGameProcess process, TargetCandidate candidate)
    {
        public IGameProcess Process { get; } = process;

        public TargetCandidate Candidate { get; set; } = candidate;
    }
}
