using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    /// <summary>When the process started, UTC; <see cref="DateTime.MinValue"/> when it couldn't be read.</summary>
    DateTime StartTime { get; }
}

/// <summary>What Game Detection reads from the system; tests fake it.</summary>
public interface IGameWindowProbe
{
    /// <summary>The foreground window when it covers its monitor with no caption and isn't the shell's; otherwise <c>null</c>.</summary>
    GameWindow? ForegroundGameWindow();

    /// <summary>The foreground window, whatever its shape, or <c>null</c> when there is none or it is the shell's.</summary>
    GameWindow? ForegroundWindow();

    /// <summary>
    /// The visible, unowned, uncloaked top-level windows that aren't tool windows or the shell's,
    /// minimized ones included, in Z order.
    /// </summary>
    IReadOnlyList<GameWindow> Windows();

    /// <summary>Opens <paramref name="processId"/> with limited access, or returns <c>null</c> when it can't be opened or its image read.</summary>
    IGameProcess? Open(int processId);
}

/// <summary>
/// The real probe (AD-12): reads only top-level windows' geometry, style and owner, and opens their
/// processes with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>; no memory reads, module lists, hooks or WinEvents.
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

        // Explorer's full-screen surfaces (Task View, the Alt+Tab host) are caption-less and cover the monitor.
        return NotTheShells(hwnd, ShellProcessId());
    }

    public GameWindow? ForegroundWindow()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        return hwnd.IsNull ? null : NotTheShells(hwnd, ShellProcessId());
    }

    public IReadOnlyList<GameWindow> Windows()
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

        var shellPid = ShellProcessId();
        var windows = new List<GameWindow>();
        foreach (var hwnd in handles)
        {
            if (!PInvoke.IsWindowVisible(hwnd)
                || IsCloaked(hwnd)
                || !PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull)
            {
                continue;
            }

            var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            if ((exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0)
            {
                continue;
            }

            if (NotTheShells(hwnd, shellPid) is { } window)
            {
                windows.Add(window);
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

    // Read once, on the limited handle (AD-12).
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

    private static uint ShellProcessId()
    {
        uint shellPid = 0;
        var shell = PInvoke.GetShellWindow();
        if (!shell.IsNull)
        {
            PInvoke.GetWindowThreadProcessId(shell, &shellPid);
        }

        return shellPid;
    }

    private static GameWindow? NotTheShells(HWND hwnd, uint shellPid)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        return pid == 0 || pid == shellPid ? null : new GameWindow((nint)hwnd.Value, (int)pid);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL Collect(HWND hwnd, LPARAM lParam)
    {
        var handles = (List<HWND>)GCHandle.FromIntPtr(lParam.Value).Target!;
        handles.Add(hwnd);
        return true;
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

    private sealed class LimitedProcess(HANDLE handle, string imagePath, DateTime startTime) : IGameProcess
    {
        private int _disposed;

        public string ImagePath { get; } = imagePath;

        public DateTime StartTime { get; } = startTime;

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
/// Game Detection (stories 3.1 to 3.3). Each poll it classifies each new process that owns a top-level
/// window once (limited open, image path, <c>Canonicalize</c>, AD-18) and detects it when its
/// foreground window covers its monitor with no caption (the fullscreen rule) or its path is listed
/// (Windows' game list or <c>manualGames</c>), even windowed. A detected game stays known, its limited
/// handle held, until its process exits; minimizing it or closing its window doesn't end it.
/// Excluded processes and those under the install folder are never detected. It only proposes
/// (AD-19), by R2's rules (AD-18): with no Target, the foreground detected game, else the best
/// (reason cold-start on the first poll, detected after); another detected game once it has held the
/// focus for <see cref="FocusToSwitch"/> (reason focus); and when the Target exits, the best of the
/// others, or none (reason exit). The best is the most recently focused, then the most recently started.
/// </summary>
public sealed class GameMonitor : IDisposable
{
    /// <summary>How long another detected game must hold the focus, without a break, to be proposed.</summary>
    public static readonly TimeSpan FocusToSwitch = TimeSpan.FromSeconds(10);

    private static readonly IReadOnlySet<string> NothingListed = new HashSet<string>();

    private readonly IGameWindowProbe _probe;
    private readonly Func<int, bool> _isExcluded;
    private readonly Action<TargetCandidate?, TargetChangeReason> _propose;
    private readonly TimeSpan _interval;
    private readonly ILogger _log;
    private readonly Func<IReadOnlySet<string>>? _listed;
    private readonly string? _installFolder;
    private readonly Action<DetectedGame>? _detected;
    private readonly Action<DetectedGame>? _exited;
    private readonly Func<int?>? _target;
    private readonly TimeProvider _time;
    private readonly ManualResetEventSlim _stop = new();

    // Poll's thread only: every process classified, until it exits; the detected ones have a Candidate.
    private readonly Dictionary<int, Seen> _seen = [];

    // Poll's thread only: the desired Target (the last proposal), and the focus streak's process and start.
    private int? _proposed;
    private TargetChangeReason _proposedReason;
    private int? _focusPid;
    private long _focusSince;
    private bool _firstPoll = true;

    private Thread? _thread;
    private int _disposed;

    /// <param name="probe">Reads the windows and opens processes.</param>
    /// <param name="isExcluded">True for a process that is never a game: the engine's and the attached App's.</param>
    /// <param name="propose">Receives each proposal, on the monitor's thread.</param>
    /// <param name="interval">The time between polls.</param>
    /// <param name="listed">The listed games' paths, each canonical or lowercase raw (AD-18); read each poll.</param>
    /// <param name="installFolder">The engine's install folder, canonical; nothing under it is detected.</param>
    /// <param name="detected">Receives each newly detected game, before any proposal of it.</param>
    /// <param name="exited">Receives each detected game whose process exited, before the exit proposal.</param>
    /// <param name="target">
    /// The committed Target's process, read each poll, so a Target whose switch is held (AD-19) is still
    /// handed over on its exit; null takes the last proposal as committed.
    /// </param>
    /// <param name="time">Times the focus streak; <see cref="TimeProvider.System"/> when null.</param>
    public GameMonitor(
        IGameWindowProbe probe,
        Func<int, bool> isExcluded,
        Action<TargetCandidate?, TargetChangeReason> propose,
        TimeSpan interval,
        ILogger log,
        Func<IReadOnlySet<string>>? listed = null,
        string? installFolder = null,
        Action<DetectedGame>? detected = null,
        Action<DetectedGame>? exited = null,
        Func<int?>? target = null,
        TimeProvider? time = null)
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
        _listed = listed;
        _installFolder = string.IsNullOrEmpty(installFolder) || installFolder.EndsWith('\\')
            ? installFolder
            : installFolder + '\\';
        _detected = detected;
        _exited = exited;
        _target = target;
        _time = time ?? TimeProvider.System;
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

        foreach (var seen in _seen.Values)
        {
            seen.Process.Dispose();
        }

        _seen.Clear();
        _stop.Dispose();
    }

    /// <summary>
    /// One poll: forgets exited processes (handing the Target over), detects new games without proposing
    /// them, follows the focus, then decides whether to propose a Target.
    /// </summary>
    internal void Poll()
    {
        var now = _time.GetTimestamp();
        ForgetExited();

        var foreground = _probe.ForegroundWindow();
        if (_probe.ForegroundGameWindow() is { } fullscreen && Classify(fullscreen.ProcessId) is { Eligible: true } game)
        {
            if (game.Candidate is null)
            {
                Detect(game, fullscreen.Handle);
            }
            else
            {
                Follow(game, fullscreen.Handle);
            }
        }

        ScanListed(foreground);
        TrackFocus(foreground, now);
        Decide(now);
        _firstPoll = false;
    }

    // The listed source over every top-level window and the foreground one: a listed game is detected
    // even windowed. Proposing is Decide's.
    private void ScanListed(GameWindow? foreground)
    {
        var listed = _listed?.Invoke() ?? NothingListed;
        var windows = _probe.Windows();

        var done = new HashSet<int>();
        // The foreground window counts only when it is one of the listed top-level windows, never an owned dialog or tool window.
        foreach (var window in foreground is { } inFront && windows.Contains(inFront) ? windows.Prepend(inFront) : windows)
        {
            if (!done.Add(window.ProcessId) || Classify(window.ProcessId) is not { Eligible: true } game)
            {
                continue;
            }

            // A process's first window here is the foreground one when it is the process's, else its first in Z order.
            if (game.Candidate is null)
            {
                if (listed.Contains(game.ProfileId!) || listed.Contains(game.RawPath))
                {
                    Detect(game, window.Handle);
                }
            }
            else if (foreground?.ProcessId == window.ProcessId)
            {
                Follow(game, window.Handle);
            }
        }
    }

    // A known game in front: its window is the one to capture from now on. The desired or committed
    // Target with a new window is proposed again, so the capture can follow it; any other game waits
    // for its focus. Proposing the committed Target while a switch is held drops that switch: it is in front.
    private void Follow(Seen game, nint window)
    {
        var moved = game.Candidate!.Window != window;
        game.Candidate = game.Candidate with { Window = window };
        if (!moved)
        {
            return;
        }

        if (_proposed == game.Pid)
        {
            // Its first reason: a switch held by a Recording still commits as what it was.
            Propose(game.Candidate, _proposedReason);
        }
        else if (Committed() == game.Pid)
        {
            Propose(game.Candidate, TargetChangeReason.Detected);
        }
    }

    // The committed Target's process: read from the orchestrator when given, else the last proposal.
    private int? Committed() => _target is null ? _proposed : _target();

    // The focus streak starts at the first poll that sees a process in front, detected or not; any other
    // foreground (another process, the shell, none) breaks it.
    private void TrackFocus(GameWindow? foreground, long now)
    {
        var pid = foreground?.ProcessId;
        if (pid != _focusPid)
        {
            _focusPid = pid;
            _focusSince = now;
        }

        if (pid is { } inFront && _seen.TryGetValue(inFront, out var seen))
        {
            seen.LastFocused = now;
        }
    }

    private void Decide(long now)
    {
        if (_proposed is null)
        {
            if (_seen.Values.Any(seen => seen.Candidate is not null))
            {
                Propose(Detected(_focusPid) ?? Best(), _firstPoll ? TargetChangeReason.ColdStart : TargetChangeReason.Detected);
            }

            return;
        }

        if (_focusPid != _proposed && Detected(_focusPid) is { } focused && _time.GetElapsedTime(_focusSince, now) >= FocusToSwitch)
        {
            Propose(focused, TargetChangeReason.Focus);
        }
    }

    private TargetCandidate? Detected(int? pid) => pid is { } p && _seen.TryGetValue(p, out var seen) ? seen.Candidate : null;

    // The most recently focused detected game, then the most recently started; null when none is detected.
    private TargetCandidate? Best() => _seen.Values
        .Where(seen => seen.Candidate is not null)
        .OrderByDescending(seen => seen.LastFocused)
        .ThenByDescending(seen => seen.Process.StartTime)
        .FirstOrDefault()?.Candidate;

    // The process's classification, cached with its limited handle until it exits; null when it is
    // excluded (never opened), can't be opened (retried next poll) or has already exited.
    private Seen? Classify(int pid)
    {
        // Checked every time: the App may attach after its window was first classified.
        if (_isExcluded(pid))
        {
            return null;
        }

        if (_seen.TryGetValue(pid, out var seen))
        {
            return seen;
        }

        var process = _probe.Open(pid);
        if (process is null)
        {
            _log.LogDebug("Process {Pid} of a window couldn't be opened; retrying next poll", pid);
            return null;
        }

        if (process.HasExited)
        {
            // Its window outlived it for a moment.
            process.Dispose();
            return null;
        }

        string? profileId;
        try
        {
            profileId = GamePaths.Canonicalize(process.ImagePath);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.LogDebug(e, "The executable of process {Pid} couldn't be canonicalized; it is never detected", pid);
            profileId = null;
        }

        var underInstall = profileId is not null && _installFolder is not null && profileId.StartsWith(_installFolder, StringComparison.Ordinal);
        seen = new Seen(pid, process, profileId, process.ImagePath.ToLowerInvariant(), profileId is not null && !underInstall);
        _seen.Add(pid, seen);
        return seen;
    }

    private void Detect(Seen game, nint window)
    {
        var candidate = game.Candidate = new TargetCandidate(game.Pid, window, game.ProfileId!, Path.GetFileName(game.Process.ImagePath));
        _log.LogInformation("Detected {Name} (process {Pid}, window 0x{Window:X}) at {ProfileId}", candidate.Name, game.Pid, window, candidate.ProfileId);
        _detected?.Invoke(ToDetectedGame(candidate));
    }

    // Forgets each exited process. When the committed Target exited, hands over to the best other game
    // (or none); when only a desired Target held from it exited, proposes the committed one again, which
    // drops the held switch.
    private void ForgetExited()
    {
        List<Seen>? exited = null;
        foreach (var seen in _seen.Values)
        {
            if (seen.Process.HasExited)
            {
                (exited ??= []).Add(seen);
            }
        }

        if (exited is null)
        {
            return;
        }

        var committed = Committed();
        var desired = _proposed;
        var committedExited = false;
        var desiredExited = false;
        foreach (var seen in exited)
        {
            _seen.Remove(seen.Pid);
            seen.Process.Dispose();
            if (seen.Candidate is not { } game)
            {
                continue;
            }

            _log.LogInformation("{Name} (process {Pid}) exited", game.Name, seen.Pid);
            _exited?.Invoke(ToDetectedGame(game));
            committedExited |= committed == seen.Pid;
            desiredExited |= desired == seen.Pid;
        }

        if (committedExited)
        {
            Propose(Best(), TargetChangeReason.Exit);
        }
        else if (desiredExited && desired != committed)
        {
            Propose(committed is null ? Best() : Detected(committed), TargetChangeReason.Exit);
        }
    }

    private void Propose(TargetCandidate? candidate, TargetChangeReason reason)
    {
        _proposed = candidate?.ProcessId;
        _proposedReason = reason;
        if (candidate is null)
        {
            _log.LogInformation("Proposing no Target Game ({Reason})", reason);
        }
        else
        {
            _log.LogInformation("Proposing {Name} (process {Pid}, window 0x{Window:X}) as the Target Game ({Reason})", candidate.Name, candidate.ProcessId, candidate.Window, reason);
        }

        _propose(candidate, reason);
    }

    private static DetectedGame ToDetectedGame(TargetCandidate candidate) => new(candidate.ProfileId, candidate.Name, candidate.ProcessId);

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

    /// <param name="Pid">The process.</param>
    /// <param name="Process">Its limited handle, held until it exits.</param>
    /// <param name="ProfileId">The canonical executable, or null when it couldn't be canonicalized (never detected).</param>
    /// <param name="RawPath">The executable as the system reports it, lowercase (AD-18).</param>
    /// <param name="Eligible">Whether it may be detected: canonicalized and not under the install folder.</param>
    private sealed record Seen(int Pid, IGameProcess Process, string? ProfileId, string RawPath, bool Eligible)
    {
        /// <summary>Set once the process is detected; it then stays detected until it exits.</summary>
        public TargetCandidate? Candidate { get; set; }

        /// <summary>The timestamp of the last poll that saw it in front; 0 = never.</summary>
        public long LastFocused { get; set; }
    }
}
