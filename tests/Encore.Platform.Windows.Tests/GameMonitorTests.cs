using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts.Messages;
using Encore.Core.Sessions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Encore.Platform.Windows.Tests;

public sealed class GameMonitorTests : IDisposable
{
    private const int GamePid = 100;
    private const int AppPid = 200;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-game-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProbe _probe = new();
    private readonly List<(TargetCandidate? To, TargetChangeReason Reason)> _proposals = [];
    private readonly List<int> _detected = [];
    private readonly List<int> _exited = [];
    private readonly HashSet<string> _listed = new(StringComparer.Ordinal);
    private readonly ManualTime _time = new();
    private readonly GameMonitor _monitor;

    public GameMonitorTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Sub"));
        Directory.CreateDirectory(Path.Combine(_folder, "Install"));
        File.WriteAllBytes(Path.Combine(_folder, "Game.EXE"), []);
        File.WriteAllBytes(Path.Combine(_folder, "Other.exe"), []);
        File.WriteAllBytes(Path.Combine(_folder, "Install", "Tool.exe"), []);
        _monitor = NewMonitor();

        // An empty first poll, so the tests' polls aren't cold starts.
        _monitor.Poll();
    }

    private string ExpectedProfileId => GamePaths.Canonicalize(Path.Combine(_folder, "Game.EXE"));

    private string GamePath => Path.Combine(_folder, "Game.EXE");

    public void Dispose()
    {
        _monitor.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private GameMonitor NewMonitor(Func<int?>? target = null) => new(
        _probe,
        pid => pid == AppPid,
        (to, reason) => _proposals.Add((to, reason)),
        TimeSpan.FromSeconds(1),
        NullLogger.Instance,
        () => _listed,
        GamePaths.Canonicalize(Path.Combine(_folder, "Install")),
        game => _detected.Add(game.ProcessId),
        game => _exited.Add(game.ProcessId),
        target,
        _time);

    private void PollAfter(GameMonitor monitor, int seconds)
    {
        _time.Advance(TimeSpan.FromSeconds(seconds));
        monitor.Poll();
    }

    private List<(int Pid, TargetChangeReason Reason)> Proposed() => [.. _proposals.Select(p => (p.To?.ProcessId ?? 0, p.Reason))];

    [Fact]
    public void CanonicalizeReturnsTheLowercaseFinalPath()
    {
        var path = GamePaths.Canonicalize(Path.Combine(_folder, "Sub", "..", "Game.EXE"));

        Assert.Equal(path.ToLowerInvariant(), path);
        Assert.False(path.StartsWith(@"\\?\", StringComparison.Ordinal));
        Assert.DoesNotContain("..", path, StringComparison.Ordinal);
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.EndsWith(@"\" + Path.GetFileName(_folder) + @"\game.exe", path, StringComparison.Ordinal);
    }

    [Fact]
    public unsafe void ABorderlessPopupOverThePrimaryMonitorIsFullscreenAndACaptionedWindowIsNot()
    {
        var width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);
        var height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN);

        // Layered with no attributes set: visible to the system, never drawn, never activated.
        const WINDOW_EX_STYLE Hidden = WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        var popup = Create(Hidden, WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_VISIBLE, width, height);
        var captioned = Create(Hidden, WINDOW_STYLE.WS_OVERLAPPEDWINDOW | WINDOW_STYLE.WS_VISIBLE, width, height);
        var small = Create(Hidden, WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_VISIBLE, width / 2, height / 2);
        try
        {
            Assert.True(ForegroundGameWindowProbe.CoversItsMonitorWithoutCaption((nint)popup.Value));
            Assert.False(ForegroundGameWindowProbe.CoversItsMonitorWithoutCaption((nint)captioned.Value));
            Assert.False(ForegroundGameWindowProbe.CoversItsMonitorWithoutCaption((nint)small.Value));
        }
        finally
        {
            PInvoke.DestroyWindow(popup);
            PInvoke.DestroyWindow(captioned);
            PInvoke.DestroyWindow(small);
        }

        static HWND Create(WINDOW_EX_STYLE exStyle, WINDOW_STYLE style, int width, int height)
        {
            HWND hwnd;
            fixed (char* className = "Static")
            {
                hwnd = PInvoke.CreateWindowEx(exStyle, className, null, style, 0, 0, width, height, HWND.Null, HMENU.Null, HINSTANCE.Null, null);
            }

            Assert.False(hwnd.IsNull);
            return hwnd;
        }
    }

    [Fact]
    public void ANewGameIsProposedOnceAndANewWindowOfItAgain()
    {
        var game = _probe.Add(GamePid, Path.Combine(_folder, "Game.EXE"));
        _probe.Foreground = new GameWindow(1, GamePid);

        _monitor.Poll();
        _monitor.Poll();
        _probe.Foreground = new GameWindow(2, GamePid);
        _monitor.Poll();

        var expected = new TargetCandidate(GamePid, 1, ExpectedProfileId, "Game.EXE");
        Assert.Equal([(expected, TargetChangeReason.Detected), (expected with { Window = 2 }, TargetChangeReason.Detected)], _proposals);
        Assert.Equal(1, game.Opens);
    }

    [Fact]
    public void ANewGameInFrontWithATargetIsOnlyDetected()
    {
        _probe.Add(GamePid, GamePath);
        _probe.Add(GamePid + 1, GamePath);
        _probe.Foreground = new GameWindow(1, GamePid);
        _monitor.Poll();

        _probe.Foreground = new GameWindow(2, GamePid + 1);
        PollAfter(_monitor, 1);
        _probe.Foreground = new GameWindow(3, GamePid + 1);
        PollAfter(_monitor, 1);

        Assert.Equal([GamePid, GamePid + 1], _detected);
        Assert.Equal([(GamePid, TargetChangeReason.Detected)], Proposed());
    }

    [Fact]
    public void AnotherGameIsProposedOnceAfterTenSecondsOfFocus()
    {
        _probe.Add(GamePid, GamePath);
        _probe.Add(GamePid + 1, GamePath);
        _listed.Add(ExpectedProfileId);
        _probe.Foreground = new GameWindow(1, GamePid);
        _probe.Windows.Add(new GameWindow(2, GamePid + 1));
        _monitor.Poll();

        // A windowed game in front: any foreground window counts as focus.
        _probe.Foreground = null;
        _probe.Front = new GameWindow(2, GamePid + 1);
        PollAfter(_monitor, 1);
        PollAfter(_monitor, 9);
        Assert.Equal([(GamePid, TargetChangeReason.Detected)], Proposed());

        PollAfter(_monitor, 1);
        PollAfter(_monitor, 1);

        Assert.Equal([(GamePid, TargetChangeReason.Detected), (GamePid + 1, TargetChangeReason.Focus)], Proposed());
        Assert.Equal(2, _proposals[1].To!.Window);
    }

    [Fact]
    public void AFullscreenVlcWindowIsNotDetected()
    {
        var vlc = Path.Combine(_folder, "vlc.exe");
        File.WriteAllBytes(vlc, []);
        _probe.Add(GamePid, vlc);
        _probe.Foreground = new GameWindow(1, GamePid);

        _monitor.Poll();
        PollAfter(_monitor, 1);

        Assert.Empty(_detected);
        Assert.Empty(_proposals);
    }

    [Fact]
    public void AFullscreenProgramUnderTheWindowsFolderIsNotDetected()
    {
        _probe.Add(GamePid, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));
        _probe.Foreground = new GameWindow(1, GamePid);

        _monitor.Poll();
        PollAfter(_monitor, 1);

        Assert.Empty(_detected);
        Assert.Empty(_proposals);
    }

    [Fact]
    public void ABorderlessUnknownAppFocusedWhileAListedGameRunsDoesNotStealTheTargetButAListedOneDoes()
    {
        var other = Path.Combine(_folder, "Other.exe");
        _probe.Add(GamePid, GamePath);
        _probe.Add(GamePid + 1, other);
        _listed.Add(ExpectedProfileId);
        _probe.Windows.Add(new GameWindow(1, GamePid));
        _probe.Front = new GameWindow(1, GamePid);
        _monitor.Poll();

        // An unknown app goes borderless fullscreen in front: detected, but it holds the focus in vain.
        _probe.Front = null;
        _probe.Foreground = new GameWindow(2, GamePid + 1);
        PollAfter(_monitor, 1);
        PollAfter(_monitor, 11);
        Assert.Equal([GamePid, GamePid + 1], _detected);
        Assert.Equal([(GamePid, TargetChangeReason.Detected)], Proposed());

        // Once it is a listed game too, its focus takes the Target.
        _listed.Add(GamePaths.Canonicalize(other));
        PollAfter(_monitor, 1);

        Assert.Equal([(GamePid, TargetChangeReason.Detected), (GamePid + 1, TargetChangeReason.Focus)], Proposed());
    }

    [Fact]
    public void ABrokenFocusStreakDoesNotSwitch()
    {
        _probe.Add(GamePid, GamePath);
        _probe.Add(GamePid + 1, GamePath);
        _listed.Add(ExpectedProfileId);
        _probe.Foreground = new GameWindow(1, GamePid);
        _probe.Windows.Add(new GameWindow(2, GamePid + 1));
        _monitor.Poll();

        _probe.Front = new GameWindow(2, GamePid + 1);
        PollAfter(_monitor, 1);
        PollAfter(_monitor, 6);
        _probe.Front = null;
        PollAfter(_monitor, 1);
        _probe.Front = new GameWindow(2, GamePid + 1);
        PollAfter(_monitor, 1);
        PollAfter(_monitor, 6);

        Assert.Equal([(GamePid, TargetChangeReason.Detected)], Proposed());
    }

    [Fact]
    public void TheTargetExitingHandsOverToTheMostRecentlyFocusedThenTheMostRecentlyStarted()
    {
        var a = _probe.Add(GamePid, GamePath);
        var b = _probe.Add(GamePid + 1, GamePath, startedAt: 10);
        var c = _probe.Add(GamePid + 2, GamePath, startedAt: 20);
        var d = _probe.Add(GamePid + 3, GamePath, startedAt: 30);
        _listed.Add(ExpectedProfileId);
        _probe.Foreground = new GameWindow(1, GamePid);
        _probe.Windows.AddRange([new GameWindow(2, GamePid + 1), new GameWindow(3, GamePid + 2), new GameWindow(4, GamePid + 3)]);
        _monitor.Poll();

        // Only C is focused; D started last.
        _probe.Foreground = null;
        _probe.Front = new GameWindow(3, GamePid + 2);
        PollAfter(_monitor, 1);
        _probe.Front = null;
        a.HasExited = true;
        PollAfter(_monitor, 1);
        c.HasExited = true;
        PollAfter(_monitor, 1);
        b.HasExited = true;
        d.HasExited = true;
        PollAfter(_monitor, 1);

        Assert.Equal(
            [(GamePid, TargetChangeReason.Detected), (GamePid + 2, TargetChangeReason.Exit), (GamePid + 3, TargetChangeReason.Exit), (0, TargetChangeReason.Exit)],
            Proposed());
    }

    [Fact]
    public void AColdStartProposesTheForegroundGameElseTheMostRecentlyStarted()
    {
        _probe.Add(GamePid, GamePath, startedAt: 10);
        _probe.Add(GamePid + 1, GamePath, startedAt: 20);
        _probe.Add(GamePid + 2, GamePath, startedAt: 30);
        _listed.Add(ExpectedProfileId);
        _probe.Windows.AddRange([new GameWindow(1, GamePid), new GameWindow(2, GamePid + 1)]);
        _probe.Foreground = new GameWindow(1, GamePid);

        using (var monitor = NewMonitor())
        {
            monitor.Poll();
        }

        _probe.Foreground = null;
        _probe.Windows.Add(new GameWindow(3, GamePid + 2));
        using (var monitor = NewMonitor())
        {
            monitor.Poll();
        }

        Assert.Equal([(GamePid, TargetChangeReason.ColdStart), (GamePid + 2, TargetChangeReason.ColdStart)], Proposed());
    }

    [Fact]
    public void WithASwitchHeldTheCommittedTargetExitingHandsOver()
    {
        var (a, _) = HoldASwitch(out var monitor);
        using (monitor)
        {
            a.HasExited = true;
            PollAfter(monitor, 1);
        }

        Assert.Equal((GamePid + 1, TargetChangeReason.Exit), Proposed()[^1]);
        Assert.Equal(3, _proposals.Count);
    }

    [Fact]
    public void WithASwitchHeldTheHeldGameExitingProposesTheCommittedTargetAgain()
    {
        var (_, b) = HoldASwitch(out var monitor);
        using (monitor)
        {
            b.HasExited = true;
            PollAfter(monitor, 1);
        }

        Assert.Equal((GamePid, TargetChangeReason.Exit), Proposed()[^1]);
        Assert.Equal(3, _proposals.Count);
    }

    [Fact]
    public void WithASwitchHeldTheCommittedTargetsNewWindowIsProposed()
    {
        HoldASwitch(out var monitor);
        using (monitor)
        {
            _probe.Foreground = _probe.Windows[0] = new GameWindow(9, GamePid);
            PollAfter(monitor, 1);
        }

        Assert.Equal((GamePid, (nint)9), (_proposals[^1].To!.ProcessId, _proposals[^1].To!.Window));
        Assert.Equal(3, _proposals.Count);
    }

    // A committed as the Target; B proposed on focus, but never committed (a Recording holds it).
    private (FakeProcess A, FakeProcess B) HoldASwitch(out GameMonitor monitor)
    {
        var a = _probe.Add(GamePid, GamePath);
        var b = _probe.Add(GamePid + 1, GamePath);
        _listed.Add(ExpectedProfileId);
        _probe.Windows.AddRange([new GameWindow(1, GamePid), new GameWindow(2, GamePid + 1)]);
        _probe.Foreground = new GameWindow(1, GamePid);
        int? committed = null;
        monitor = NewMonitor(() => committed);
        monitor.Poll();
        committed = GamePid;

        _probe.Foreground = new GameWindow(2, GamePid + 1);
        PollAfter(monitor, 1);
        PollAfter(monitor, 10);
        _probe.Foreground = null;
        Assert.Equal([(GamePid, TargetChangeReason.ColdStart), (GamePid + 1, TargetChangeReason.Focus)], Proposed());
        return (a, b);
    }

    [Fact]
    public void AProcessThatCannotBeOpenedIsRetriedNextPoll()
    {
        _probe.Foreground = new GameWindow(1, GamePid);
        _monitor.Poll();
        Assert.Empty(_proposals);

        _probe.Add(GamePid, Path.Combine(_folder, "Game.EXE"));
        _monitor.Poll();

        Assert.Equal(GamePid, Assert.Single(_proposals).To!.ProcessId);
    }

    [Fact]
    public void AnExcludedProcessIsNeverOpenedOrProposed()
    {
        var app = _probe.Add(AppPid, Path.Combine(_folder, "Game.EXE"));
        _probe.Foreground = new GameWindow(1, AppPid);

        _monitor.Poll();

        Assert.Empty(_proposals);
        Assert.Equal(0, app.Opens);
    }

    [Fact]
    public void TheTargetExitingProposesNoTargetAndIsForgotten()
    {
        var game = _probe.Add(GamePid, Path.Combine(_folder, "Game.EXE"));
        _probe.Foreground = new GameWindow(1, GamePid);
        _monitor.Poll();

        _probe.Foreground = null;
        game.HasExited = true;
        _monitor.Poll();
        _monitor.Poll();

        Assert.Equal(((TargetCandidate?)null, TargetChangeReason.Exit), _proposals[^1]);
        Assert.Equal(2, _proposals.Count);
        Assert.True(game.Disposed);
    }

    [Fact]
    public void AWindowedListedGameIsDetectedAndProposed()
    {
        _probe.Add(GamePid, Path.Combine(_folder, "Game.EXE"));
        _listed.Add(ExpectedProfileId);
        _probe.Windows.Add(new GameWindow(5, GamePid));

        _monitor.Poll();

        Assert.Equal([GamePid], _detected);
        Assert.Equal([(new TargetCandidate(GamePid, 5, ExpectedProfileId, "Game.EXE"), TargetChangeReason.Detected)], _proposals);
    }

    [Fact]
    public void ADetectedGameStaysKnownWithNoWindowUntilItsExit()
    {
        var game = _probe.Add(GamePid, Path.Combine(_folder, "Game.EXE"));
        _listed.Add(ExpectedProfileId);
        _probe.Windows.Add(new GameWindow(5, GamePid));
        _monitor.Poll();

        _probe.Windows.Clear();
        _monitor.Poll();
        _monitor.Poll();
        Assert.Single(_proposals);
        Assert.Empty(_exited);

        game.HasExited = true;
        _monitor.Poll();

        Assert.Equal([GamePid], _exited);
        Assert.Equal(((TargetCandidate?)null, TargetChangeReason.Exit), _proposals[^1]);
        Assert.Equal(2, _proposals.Count);
    }

    [Fact]
    public void NothingUnderTheInstallFolderIsDetected()
    {
        var tool = Path.Combine(_folder, "Install", "Tool.exe");
        _probe.Add(GamePid, tool);
        _probe.Add(GamePid + 1, tool);
        _listed.Add(GamePaths.Canonicalize(tool));
        _probe.Foreground = new GameWindow(1, GamePid);
        _probe.Windows.Add(new GameWindow(2, GamePid + 1));

        _monitor.Poll();
        _monitor.Poll();

        Assert.Empty(_detected);
        Assert.Empty(_proposals);
    }

    [Fact]
    public void AnUnlistedWindowsProcessIsOpenedOnceAndNeverProposed()
    {
        var other = _probe.Add(GamePid, Path.Combine(_folder, "Other.exe"));
        _listed.Add(ExpectedProfileId);
        _probe.Windows.Add(new GameWindow(7, GamePid));

        _monitor.Poll();
        _monitor.Poll();
        _monitor.Poll();

        Assert.Equal(1, other.Opens);
        Assert.Empty(_detected);
        Assert.Empty(_proposals);
    }

    [Fact]
    public void GameConfigStoreReadsTheRealListAsLowercasePaths()
    {
        var store = new GameConfigStore(NullLogger.Instance);

        var paths = store.Paths();

        Assert.All(paths, path => Assert.Equal(path.ToLowerInvariant(), path));
        Assert.Same(paths, store.Paths());
    }

    private sealed class FakeProbe : IGameWindowProbe
    {
        private readonly Dictionary<int, FakeProcess> _processes = [];

        /// <summary>The fullscreen foreground window.</summary>
        public GameWindow? Foreground { get; set; }

        /// <summary>The foreground window of any shape; the fullscreen one when unset.</summary>
        public GameWindow? Front { get; set; }

        public List<GameWindow> Windows { get; } = [];

        public FakeProcess Add(int pid, string imagePath, int startedAt = 0) =>
            _processes[pid] = new FakeProcess(imagePath) { StartTime = DateTime.UnixEpoch.AddSeconds(startedAt) };

        public GameWindow? ForegroundGameWindow() => Foreground;

        public GameWindow? ForegroundWindow() => Front ?? Foreground;

        IReadOnlyList<GameWindow> IGameWindowProbe.Windows() => Windows;

        public IGameProcess? Open(int processId)
        {
            if (!_processes.TryGetValue(processId, out var process))
            {
                return null;
            }

            process.Opens++;
            return process;
        }
    }

    private sealed class FakeProcess(string imagePath) : IGameProcess
    {
        public string ImagePath { get; } = imagePath;

        public bool HasExited { get; set; }

        public DateTime StartTime { get; init; }

        public int Opens { get; set; }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class ManualTime : TimeProvider
    {
        // Never 0: a game's last focus of 0 means never.
        private long _ticks = TimeSpan.TicksPerHour;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
