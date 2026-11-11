using Microsoft.Extensions.Logging;
using Encore.Contracts.Messages;
using Encore.Core.Sessions;

namespace Encore.Platform.Windows;

/// <summary>Game Detection: polls the windows, detects games and proposes which one to capture. Rules: docs/code-notes.md.</summary>
public sealed partial class GameMonitor : IDisposable
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
    private readonly Func<string, bool>? _isIgnored;
    private readonly Func<string, string>? _profileOf;
    private readonly ManualResetEventSlim _stop = new();

    // Poll's thread only: every process classified, until it exits; the detected ones have a Candidate.
    private readonly Dictionary<int, SeenProcess> _seen = [];

    // Poll's thread only: the desired Target (the last proposal), and the focus streak's process and start.
    private int? _proposed;
    private TargetChangeReason _proposedReason;
    private int? _focusProcessId;
    private long _focusSince;

    // Poll's thread only: the listed games' paths this poll read.
    private IReadOnlySet<string> _listedNow = NothingListed;
    private bool _firstPoll = true;

    private Thread? _thread;
    private int _disposed;

    /// <summary>Every callback runs on the monitor's thread; listed, target and isIgnored are read on each poll.</summary>
    /// <param name="profileOf">Maps a program's canonical path to the Game Profile id it belongs to; null keeps the path.</param>
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
        Func<string, bool>? isIgnored = null,
        Func<string, string>? profileOf = null)
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
        _profileOf = profileOf;
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

    private static DetectedGame ToDetectedGame(TargetCandidate candidate) => new(candidate.ProfileId, candidate.Name, candidate.ProcessId);

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
                if (IsListed(game, listed) && !Ignored(game))
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

    // The streak starts at the first poll that sees a process in front; any other foreground breaks it.
    private void TrackFocus(GameWindow? foreground, long now)
    {
        var processId = foreground?.ProcessId;
        if (processId != _focusProcessId)
        {
            _focusProcessId = processId;
            _focusSince = now;
        }

        if (processId is { } inFront && _seen.TryGetValue(inFront, out var seen))
        {
            seen.LastFocused = now;
        }
    }

    // A process's classification, cached until it exits; null when it is excluded, can't be opened or has exited.
    private SeenProcess? Classify(int processId)
    {
        // Checked every time: the App may attach after its window was first classified.
        if (_isExcluded(processId))
        {
            return null;
        }

        if (_seen.TryGetValue(processId, out var seen))
        {
            return seen;
        }

        if (OpenLive(processId) is not { } process)
        {
            return null;
        }

        var path = CanonicalPathOf(process, processId);
        var underInstall = path is not null
            && _installFolder is not null
            && path.StartsWith(_installFolder, StringComparison.Ordinal);
        var rawPath = process.ImagePath.ToLowerInvariant();
        var profileId = path is null ? null : _profileOf?.Invoke(path) ?? path;
        seen = new SeenProcess(processId, process, profileId, rawPath, path is not null && !underInstall)
        {
            Path = path,
            NonGame = NeverGames.Contains(path ?? rawPath),
        };
        _seen.Add(processId, seen);
        return seen;
    }

    // Null when the process can't be opened, or has already exited and only its window outlived it for a moment.
    private IGameProcess? OpenLive(int processId)
    {
        var process = _probe.Open(processId);
        if (process is null)
        {
            _log.LogDebug("Process {Pid} of a window couldn't be opened; retrying next poll", processId);
            return null;
        }

        if (process.HasExited)
        {
            process.Dispose();
            return null;
        }

        return process;
    }

    // The canonical executable path; null, and never detected, when it can't be made canonical.
    private string? CanonicalPathOf(IGameProcess process, int processId)
    {
        try
        {
            return GamePaths.Canonicalize(process.ImagePath);
        }
        catch (Exception e) when (GamePaths.CanonicalizeFailed(e))
        {
            _log.LogDebug(e, "The executable of process {Pid} couldn't be canonicalized; it is never detected", processId);
            return null;
        }
    }

    private void Detect(SeenProcess game, nint window)
    {
        var candidate = game.Candidate = new TargetCandidate(game.Pid, window, game.ProfileId!, Path.GetFileName(game.Process.ImagePath));
        _log.LogInformation(
            "Detected {Name} (process {Pid}, window 0x{Window:X}) at {ProfileId}", candidate.Name, game.Pid, window, candidate.ProfileId);
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
        List<SeenProcess>? exited = null;
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

    private bool Ignored(SeenProcess game) => _isIgnored is not null && _isIgnored(game.ProfileId!);

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
}
