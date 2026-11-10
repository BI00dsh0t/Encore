using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Encore.Contracts.Messages;
using Encore.Core.Sessions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Encore.Platform.Windows;

/// <summary>A window that may be a game's, and the process that owns it.</summary>
public readonly record struct GameWindow(nint Handle, int ProcessId);

/// <summary>A game's process, opened with <c>PROCESS_QUERY_LIMITED_INFORMATION</c> only.</summary>
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
    /// <summary>The foreground window when it covers its monitor with no caption and isn't the shell's; otherwise null.</summary>
    GameWindow? ForegroundGameWindow();

    /// <summary>The foreground window, whatever its shape, or null when there is none or it is the shell's.</summary>
    GameWindow? ForegroundWindow();

    /// <summary>Visible, unowned, uncloaked top-level windows in Z order, minimized ones included; no tool or shell windows.</summary>
    IReadOnlyList<GameWindow> Windows();

    /// <summary>Opens a process with limited access; null when it can't be opened or its image path read.</summary>
    IGameProcess? Open(int processId);
}

/// <summary>The real probe: reads only window geometry, style and owner, and opens processes with limited access. No hooks.</summary>
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

    /// <summary>Whether a window is borderless or fullscreen: visible, no caption, not the shell's, and covering its monitor.</summary>
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

/// <summary>Game Detection: polls the windows, detects games and proposes which one to capture. Rules: docs/code-notes.md.</summary>
public sealed class GameMonitor : IDisposable
{
    /// <summary>How long another detected game must hold the focus, without a break, to be proposed.</summary>
    public static readonly TimeSpan FocusToSwitch = TimeSpan.FromSeconds(10);

    private static readonly IReadOnlySet<string> NothingListed = new HashSet<string>();

    /// <summary>Executables the fullscreen rule never detects as games, matched on the file name, ignoring case.</summary>
    public static IReadOnlySet<string> KnownNonGames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Browsers.
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe", "vivaldi.exe", "iexplore.exe",

        // Media players.
        "vlc.exe", "mpc-hc.exe", "mpc-hc64.exe", "mpc-be.exe", "mpc-be64.exe", "mpv.exe", "wmplayer.exe", "video.ui.exe",
        "microsoft.media.player.exe", "potplayer.exe", "potplayer64.exe", "potplayermini.exe", "potplayermini64.exe", "ffplay.exe",

        // Chat, the shell and Office.
        "discord.exe", "explorer.exe", "applicationframehost.exe", "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe",
        "onenote.exe", "msaccess.exe", "mspub.exe", "visio.exe", "teams.exe", "ms-teams.exe",

        // Encore itself, wherever it runs from (a dev build isn't under the install folder).
        "encore.app.exe", "encore.engine.exe",

        // ReplayBuffer, the app before its rebrand to Encore, if an old copy still runs.
        "replaybuffer.app.exe", "replaybuffer.engine.exe",
    };

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
    private readonly Func<string, bool>? _isIgnored;
    private readonly ManualResetEventSlim _stop = new();

    // Poll's thread only: every process classified, until it exits; the detected ones have a Candidate.
    private readonly Dictionary<int, Seen> _seen = [];

    // Poll's thread only: the desired Target (the last proposal), and the focus streak's process and start.
    private int? _proposed;
    private TargetChangeReason _proposedReason;
    private int? _focusPid;
    private long _focusSince;

    // Poll's thread only: the listed games' paths this poll read.
    private IReadOnlySet<string> _listedNow = NothingListed;
    private bool _firstPoll = true;

    private Thread? _thread;
    private int _disposed;

    /// <summary>Every callback runs on the monitor's thread; listed, target and isIgnored are read on each poll.</summary>
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
        TimeProvider? time = null,
        Func<string, bool>? isIgnored = null)
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
        _isIgnored = isIgnored;
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

    /// <summary>One poll: forget exited and Ignored games, detect new ones, follow the focus, then decide what to propose.</summary>
    internal void Poll()
    {
        var now = _time.GetTimestamp();
        ForgetExited();
        ForgetIgnored();

        var foreground = _probe.ForegroundWindow();
        if (_probe.ForegroundGameWindow() is { } fullscreen && Classify(fullscreen.ProcessId) is { Eligible: true } game)
        {
            if (game.Candidate is null)
            {
                // A known non-game passes the fullscreen rule showing a video; only a listing detects it.
                if (!Ignored(game) && !game.NonGame)
                {
                    Detect(game, fullscreen.Handle);
                }
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

    // A listed game is detected even when windowed; proposing is left to Decide.
    private void ScanListed(GameWindow? foreground)
    {
        var listed = _listedNow = _listed?.Invoke() ?? NothingListed;
        var windows = _probe.Windows();

        var done = new HashSet<int>();
        // The foreground window counts only when it is a listed top-level window, never a dialog or tool window.
        foreach (var window in foreground is { } inFront && windows.Contains(inFront) ? windows.Prepend(inFront) : windows)
        {
            if (!done.Add(window.ProcessId) || Classify(window.ProcessId) is not { Eligible: true } game)
            {
                continue;
            }

            // A process's first window here is the foreground one when it is the process's, else its first in Z order.
            if (game.Candidate is null)
            {
                if ((listed.Contains(game.ProfileId!) || listed.Contains(game.RawPath)) && !Ignored(game))
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

    // A known game in front: capture its window from now on, and propose it again when it is the Target.
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

    // The streak starts at the first poll that sees a process in front; any other foreground breaks it.
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

        if (_focusPid != _proposed
            && Detected(_focusPid) is { } focused
            && _time.GetElapsedTime(_focusSince, now) >= FocusToSwitch
            && !Outranks(_proposed, focused.ProcessId))
        {
            Propose(focused, TargetChangeReason.Focus);
        }
    }

    // Only another listed game takes the Target from a listed game by focus.
    private bool Outranks(int? target, int focused) =>
        target is { } pid
        && _seen.TryGetValue(pid, out var current)
        && current.Candidate is not null
        && IsListed(current)
        && !(_seen.TryGetValue(focused, out var other) && IsListed(other));

    private bool IsListed(Seen game) =>
        (game.ProfileId is { } id && _listedNow.Contains(id)) || _listedNow.Contains(game.RawPath);

    private TargetCandidate? Detected(int? pid) => pid is { } p && _seen.TryGetValue(p, out var seen) ? seen.Candidate : null;

    // The most recently focused detected game, then the most recently started; null when none is detected.
    private TargetCandidate? Best() => _seen.Values
        .Where(seen => seen.Candidate is not null)
        .OrderByDescending(seen => seen.LastFocused)
        .ThenByDescending(seen => seen.Process.StartTime)
        .FirstOrDefault()?.Candidate;

    // A process's classification, cached until it exits; null when it is excluded, can't be opened or has exited.
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
        catch (Exception e) when (GamePaths.CanonicalizeFailed(e))
        {
            _log.LogDebug(e, "The executable of process {Pid} couldn't be canonicalized; it is never detected", pid);
            profileId = null;
        }

        var underInstall = profileId is not null && _installFolder is not null && profileId.StartsWith(_installFolder, StringComparison.Ordinal);
        seen = new Seen(pid, process, profileId, process.ImagePath.ToLowerInvariant(), profileId is not null && !underInstall)
        {
            NonGame = KnownNonGames.Contains(Path.GetFileName(process.ImagePath)),
        };
        _seen.Add(pid, seen);
        return seen;
    }

    private void Detect(Seen game, nint window)
    {
        var candidate = game.Candidate = new TargetCandidate(game.Pid, window, game.ProfileId!, Path.GetFileName(game.Process.ImagePath));
        _log.LogInformation("Detected {Name} (process {Pid}, window 0x{Window:X}) at {ProfileId}", candidate.Name, game.Pid, window, candidate.ProfileId);
        _detected?.Invoke(ToDetectedGame(candidate));

        // The detected callback may have just made its Game Profile Ignored: forget it now, so it isn't proposed this poll.
        if (Ignored(game))
        {
            game.Candidate = null;
            _log.LogInformation("{Name} (process {Pid}) is Ignored", candidate.Name, game.Pid);
            _exited?.Invoke(ToDetectedGame(candidate));
        }
    }

    // Forgets each exited process, and hands the Target over when the Target was the one that exited.
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

        HandOver(committed, desired, committedExited, desiredExited, TargetChangeReason.Exit);
    }

    // Un-detects games whose profile is now Ignored, but keeps them classified so clearing the flag detects them again.
    private void ForgetIgnored()
    {
        if (_isIgnored is null)
        {
            return;
        }

        var committed = Committed();
        var desired = _proposed;
        var committedIgnored = false;
        var desiredIgnored = false;
        foreach (var seen in _seen.Values)
        {
            if (seen.Candidate is not { } game || !Ignored(seen))
            {
                continue;
            }

            seen.Candidate = null;
            _log.LogInformation("{Name} (process {Pid}) is Ignored", game.Name, seen.Pid);
            _exited?.Invoke(ToDetectedGame(game));
            committedIgnored |= committed == seen.Pid;
            desiredIgnored |= desired == seen.Pid;
        }

        HandOver(committed, desired, committedIgnored, desiredIgnored, TargetChangeReason.Ignored);
    }

    // Hands over to the best other game when the Target is gone; a lost held switch re-proposes the committed Target.
    private void HandOver(int? committed, int? desired, bool committedGone, bool desiredGone, TargetChangeReason reason)
    {
        if (committedGone)
        {
            Propose(Best(), reason);
        }
        else if (desiredGone && desired != committed)
        {
            Propose(committed is null ? Best() : Detected(committed), reason);
        }
    }

    private bool Ignored(Seen game) => _isIgnored is not null && _isIgnored(game.ProfileId!);

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

    /// <summary>One classified process; ProfileId is null, and it is never detected, when its path can't be made canonical.</summary>
    private sealed record Seen(int Pid, IGameProcess Process, string? ProfileId, string RawPath, bool Eligible)
    {
        /// <summary>Set once the process is detected; it then stays detected until it exits or its profile is Ignored.</summary>
        public TargetCandidate? Candidate { get; set; }

        /// <summary>The timestamp of the last poll that saw it in front; 0 = never.</summary>
        public long LastFocused { get; set; }

        /// <summary>Whether it is one of <see cref="KnownNonGames"/>: the fullscreen rule never detects it.</summary>
        public bool NonGame { get; init; }
    }
}
