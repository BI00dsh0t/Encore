using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Overlay;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class OverlayTests
{
    private static readonly CaptureState Buffering = CaptureState.Initial with { Replay = ReplayState.Buffering };

    [Fact]
    public void TheDotFollowsTheCaptureState()
    {
        var engine = new FakeEngineSession();
        var model = new OverlayViewModel(engine, action => action());
        Assert.Equal(OverlayDot.None, model.Dot);

        Assert.Equal(OverlayDot.Hollow, After(engine, model, Buffering));
        Assert.Equal(OverlayDot.Hollow, After(engine, model, CaptureState.Initial with { Replay = ReplayState.Draining }));
        Assert.Equal(OverlayDot.Solid, After(engine, model, Buffering with { Recording = RecordingState.Running }));
        Assert.Equal(OverlayDot.Solid, After(engine, model, CaptureState.Initial with { MonitorRecording = RecordingState.Running }));
        Assert.Equal(OverlayDot.None, After(engine, model, CaptureState.Initial));

        // The Overlay sits on the Target Game's monitor, so it follows the Target's process.
        engine.State = engine.State! with { TargetGame = new TargetGame("game", "game.exe", 77) };
        Assert.Equal(OverlayDot.Hollow, After(engine, model, Buffering));
        Assert.Equal(77, model.TargetProcessId);

        engine.Status = EngineStatus.Disconnected;
        engine.RaiseChanged();
        Assert.Equal(OverlayDot.None, model.Dot);
        Assert.Null(model.TargetProcessId);
    }

    [Fact]
    public void TheSavingIconLastsUntilTheLaterOfTheCooldownAndTheWrite()
    {
        var time = new ManualTime();
        var engine = new FakeEngineSession();
        var model = new OverlayViewModel(engine, action => action(), time);
        Assert.False(model.Saving);

        // Writing, in the cooldown; the cooldown passes with the write still going.
        Shift(engine, Buffering with { CooldownUntil = time.GetUtcNow().AddSeconds(5), ClipsWriting = 1 });
        Assert.True(model.Saving);
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(model.Saving);
        Shift(engine, engine.State!.Capture with { ClipsWriting = 0 });
        Assert.False(model.Saving);

        // The write ended first: the timer reads the session again at the cooldown's end.
        Shift(engine, Buffering with { CooldownUntil = time.GetUtcNow().AddSeconds(5) });
        Assert.True(model.Saving);
        time.Advance(TimeSpan.FromSeconds(4.9));
        Assert.True(model.Saving);
        time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.False(model.Saving);
    }

    [Fact]
    public void AReconnectMidCooldownShowsTheSavingIconAndADisconnectHidesIt()
    {
        var time = new ManualTime();
        var engine = new FakeEngineSession();
        engine.State = engine.State! with { Capture = CaptureState.Initial with { CooldownUntil = time.GetUtcNow().AddSeconds(3) } };

        var model = new OverlayViewModel(engine, action => action(), time);
        Assert.True(model.Saving);

        engine.Status = EngineStatus.Disconnected;
        engine.RaiseChanged();
        Assert.False(model.Saving);
    }

    [Fact]
    public void QueuedNoticesShowOneAtATimeInTheOrderRaised()
    {
        var time = new ManualTime();
        var engine = new FakeEngineSession();
        var model = new OverlayViewModel(engine, action => action(), time);
        var now = time.GetUtcNow();
        Raise(engine, Notice("a", "clip-saved", WarningSeverity.Info, "Clip saved", now));
        Raise(engine, Notice("b", "clip-failed", WarningSeverity.Error, "Clip not saved: save failed", now.AddMilliseconds(100)));
        Raise(engine, Notice("c", "recording-started", WarningSeverity.Info, "Recording started", now.AddMilliseconds(200)));

        foreach (var (id, severity) in new[] { ("a", WarningSeverity.Info), ("b", WarningSeverity.Error), ("c", WarningSeverity.Info) })
        {
            Assert.Equal(id, model.Message?.Id);
            Assert.Equal(severity, model.Message!.Severity);
            time.Advance(TimeSpan.FromSeconds(3.9));
            Assert.Equal(id, model.Message?.Id);
            time.Advance(TimeSpan.FromSeconds(0.1));
        }

        Assert.Null(model.Message);
    }

    [Fact]
    public void ADismissedNoticeAndANonOverlayWarningNeverShow()
    {
        var time = new ManualTime();
        var engine = new FakeEngineSession();
        var model = new OverlayViewModel(engine, action => action(), time);
        var now = time.GetUtcNow();
        Raise(
            engine,
            Notice("a", "clip-saved", WarningSeverity.Info, "Clip saved", now),
            Notice("b", "recording-started", WarningSeverity.Info, "Recording started", now.AddMilliseconds(100)),
            Notice("q", "feedback-device-captured", WarningSeverity.Warning, "The feedback device is captured", now.AddMilliseconds(150)) with { Overlay = false });
        Assert.Equal("a", model.Message?.Id);

        Dismiss(engine, "b");
        Assert.Equal("a", model.Message?.Id);
        time.Advance(OverlayViewModel.MessageDuration);
        Assert.Null(model.Message);
    }

    [Fact]
    public void AReconnectShowsTheWaitingNoticeButNeverReplaysAShownOne()
    {
        var time = new ManualTime();
        var engine = new FakeEngineSession();
        var model = new OverlayViewModel(engine, action => action(), time);
        var now = time.GetUtcNow();
        Raise(
            engine,
            Notice("a", "clip-saved", WarningSeverity.Info, "Clip saved", now),
            Notice("b", "recording-started", WarningSeverity.Info, "Recording started", now.AddMilliseconds(100)));
        Assert.Equal("a", model.Message?.Id);

        engine.Status = EngineStatus.Disconnected;
        engine.RaiseChanged();
        Assert.Null(model.Message);

        time.Advance(TimeSpan.FromSeconds(10));
        engine.Status = EngineStatus.Connected;
        engine.RaiseChanged();
        Assert.Equal("b", model.Message?.Id);
        time.Advance(OverlayViewModel.MessageDuration);
        Assert.Null(model.Message);
    }

    [Fact]
    public void ARepeatOfAWaitingFailureShowsOnce()
    {
        var time = new ManualTime();
        var engine = new FakeEngineSession();
        var model = new OverlayViewModel(engine, action => action(), time);
        var now = time.GetUtcNow();
        Raise(engine, Notice("a", "clip-saved", WarningSeverity.Info, "Clip saved", now));
        Raise(engine, Notice("x", "clip-failed", WarningSeverity.Error, "Clip not saved: save failed", now.AddMilliseconds(100)));
        Raise(engine, Notice("x2", "clip-failed", WarningSeverity.Error, "Clip not saved: save failed", now.AddMilliseconds(200)));

        // The first failure's dismissal doesn't drop the repeat that took its place.
        Dismiss(engine, "x");
        time.Advance(OverlayViewModel.MessageDuration);
        Assert.Equal("x2", model.Message?.Id);
        time.Advance(OverlayViewModel.MessageDuration);
        Assert.Null(model.Message);
    }

    [Fact]
    public void WithoutOverlayPlanesTheDotShowsForThreeSecondsAfterEachChange()
    {
        var time = new ManualTime();
        var engine = new FakeEngineSession();
        var model = new OverlayViewModel(engine, action => action(), time, overlayPlanes: _ => false);

        Shift(engine, Buffering with { MicMuted = true });
        Assert.False(model.DotAlways);
        Assert.Equal((OverlayDot.Hollow, true), (model.Dot, model.MicMuted));
        time.Advance(OverlayViewModel.DotOnChangeDuration);
        Assert.Equal((OverlayDot.None, false), (model.Dot, model.MicMuted));

        // A timer that fires before the clock reaches the end arms again for what's left.
        Shift(engine, Buffering with { MicMuted = false });
        time.Advance(OverlayViewModel.DotOnChangeDuration - TimeSpan.FromMilliseconds(1), clockLags: TimeSpan.FromMilliseconds(1));
        Assert.Equal(OverlayDot.Hollow, model.Dot);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(OverlayDot.None, model.Dot);

        // Unmuting is a change too.
        Shift(engine, Buffering with { MicMuted = true });
        time.Advance(OverlayViewModel.DotOnChangeDuration);
        Shift(engine, Buffering with { MicMuted = false });
        Assert.Equal(OverlayDot.Hollow, model.Dot);
        time.Advance(OverlayViewModel.DotOnChangeDuration);
        Assert.Equal(OverlayDot.None, model.Dot);

        // overlayDot: always shows it for as long as it lasts, whatever the monitor.
        SetOverlayDot(engine, OverlayDotMode.Always);
        Assert.Equal(OverlayDot.Hollow, model.Dot);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(OverlayDot.Hollow, model.Dot);
    }

    [Fact]
    public void WithOverlayPlanesTheDotStaysUnlessOnChangeIsChosenAndADisplayChangeAsksAgain()
    {
        var time = new ManualTime();
        var engine = new FakeEngineSession();
        var asked = new List<int?>();
        var planes = true;
        var model = new OverlayViewModel(engine, action => action(), time, overlayPlanes: pid =>
        {
            asked.Add(pid);
            return planes;
        });

        engine.State = engine.State! with { TargetGame = new TargetGame("game", "game.exe", 77) };
        Shift(engine, Buffering);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(OverlayDot.Hollow, model.Dot);
        Assert.True(model.DotAlways);

        // Asked once per Target Game (none at first, then process 77), and again when the monitors change.
        Assert.Equal([null, 77], asked);
        var count = asked.Count;
        planes = false;
        model.DisplaysChanged();
        Assert.Equal(count + 1, asked.Count);
        Assert.Equal(OverlayDot.None, model.Dot);

        // onChange shows it on a change only, even with planes.
        planes = true;
        model.DisplaysChanged();
        SetOverlayDot(engine, OverlayDotMode.OnChange);
        Assert.Equal(OverlayDot.None, model.Dot);
        Shift(engine, Buffering with { Recording = RecordingState.Running });
        Assert.Equal(OverlayDot.Solid, model.Dot);
        time.Advance(OverlayViewModel.DotOnChangeDuration);
        Assert.Equal(OverlayDot.None, model.Dot);
    }

    [Fact]
    public void AnUnknownPlaneAnswerShowsTheDotOnChangeAndIsAskedAgain()
    {
        var time = new ManualTime();
        var engine = new FakeEngineSession();
        bool? planes = null;
        var model = new OverlayViewModel(engine, action => action(), time, overlayPlanes: _ => planes);
        Shift(engine, Buffering);
        Assert.False(model.DotAlways);

        planes = true;
        time.Advance(OverlayViewModel.UnknownPlanesRetry);
        engine.RaiseChanged();
        Assert.True(model.DotAlways);
    }

    [Fact]
    public void ThePlaneQueryRunsInTheBackgroundAndItsAnswerIsPostedBack()
    {
        var time = new ManualTime();
        var engine = new FakeEngineSession();
        var queued = new List<Action>();
        var model = new OverlayViewModel(engine, action => action(), time, overlayPlanes: _ => true, background: queued.Add);

        // Until it answers, the monitor is taken to have no planes.
        Shift(engine, Buffering);
        Assert.False(model.DotAlways);
        Assert.Single(queued);

        queued[0]();
        Assert.True(model.DotAlways);
        time.Advance(OverlayViewModel.DotOnChangeDuration);
        Assert.Equal(OverlayDot.Hollow, model.Dot);
    }

    [Fact]
    public void TheTrayShowsTheCaptureStateWhateverTheOverlayDot()
    {
        var engine = new FakeEngineSession();
        Assert.Equal("Not capturing", TrayCapture.Describe(TrayCapture.For(engine)));

        engine.State = engine.State! with { Capture = Buffering };
        Assert.Equal("Ready to save Clips", TrayCapture.Describe(TrayCapture.For(engine)));
        engine.State = engine.State with { Capture = Buffering with { Recording = RecordingState.Running } };
        Assert.Equal("Recording", TrayCapture.Describe(TrayCapture.For(engine)));

        engine.Status = EngineStatus.Disconnected;
        Assert.Equal(OverlayDot.None, TrayCapture.For(engine));
    }

    [Fact]
    public void TheMicIconFollowsTheMuteWhileTheDotShows()
    {
        var engine = new FakeEngineSession();
        var model = new OverlayViewModel(engine, action => action(), new ManualTime());

        // The muted default with nothing running shows nothing.
        Assert.True(CaptureState.Initial.MicMuted);
        Assert.False(model.MicMuted);

        Shift(engine, Buffering with { MicMuted = true });
        Assert.True(model.MicMuted);
        Shift(engine, Buffering with { MicMuted = false });
        Assert.False(model.MicMuted);
    }

    [Fact]
    public void TheRedXShowsWhileADiskFullWarningIsActive()
    {
        var engine = new FakeEngineSession();
        var model = new OverlayViewModel(engine, action => action(), new ManualTime());
        Assert.False(model.DiskFull);

        Raise(engine, Notice("d", ErrorCodes.DiskFull, WarningSeverity.Error, "The disk is full", DateTimeOffset.UtcNow));
        Assert.True(model.DiskFull);
        Assert.Equal(OverlayDot.None, model.Dot);

        Dismiss(engine, "d");
        Assert.False(model.DiskFull);
    }

    [Fact]
    public void ADisplayChangePlacesTheWindowOnTheNewPrimaryAtItsDpi() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        var primary = new MonitorArea(0, 0, 1920, 1080, 96);
        var model = new OverlayViewModel(engine, action => action(), new ManualTime());
        var window = new OverlayWindow(model, _ => primary);
        try
        {
            Shift(engine, Buffering);
            AssertDotOn(primary);

            // The primary moves to a 150 % monitor (T-03): nothing the Overlay shows changed, but it moves there.
            primary = new MonitorArea(0, 0, 1600, 900, 144);
            model.DisplaysChanged();
            AssertDotOn(primary);
        }
        finally
        {
            window.Close();
        }

        // The dot's corner, as OverlayWindow.Place puts it: the strip of three icon slots extends it left.
        void AssertDotOn(MonitorArea monitor)
        {
            var (x, y, size) = Monitors.TopRight(monitor, OverlayWindow.DotSizeDip, OverlayWindow.InsetDip);
            var left = x - (int)Math.Round(3 * (16 + 6) * (size / OverlayWindow.DotSizeDip));
            var bounds = OverlayWindowStyle.Bounds(window.Handle);
            Assert.Equal((left, y), (bounds.Left, bounds.Top));
        }
    });

    [Fact]
    public void TheWindowIsTopmostClickThroughNeverActivatedAndExcludedFromCapture() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        var time = new ManualTime();
        var window = new OverlayWindow(new OverlayViewModel(engine, action => action(), time));
        try
        {
            var hwnd = window.Handle;
            Assert.Equal(OverlayWindowStyle.Styles, OverlayWindowStyle.ExtendedStyle(hwnd) & OverlayWindowStyle.Styles);
            Assert.True(CaptureExclusion.IsApplied(hwnd));
            Assert.False(window.IsVisible);

            Shift(engine, Buffering);
            Assert.True(window.IsVisible);
            Assert.Equal(OverlayWindowStyle.Styles, OverlayWindowStyle.ExtendedStyle(hwnd) & OverlayWindowStyle.Styles);

            Shift(engine, CaptureState.Initial);
            Assert.False(window.IsVisible);

            // Saving with no dot still shows the window.
            Shift(engine, CaptureState.Initial with { ClipsWriting = 1 });
            Assert.True(window.IsVisible);

            Shift(engine, CaptureState.Initial);
            Assert.False(window.IsVisible);

            // A message with no dot or icon shows the window too.
            Raise(engine, Notice("a", "clip-saved", WarningSeverity.Info, "Clip saved", DateTimeOffset.UtcNow));
            Assert.True(window.IsVisible);

            Dismiss(engine, "a");
            Assert.False(window.IsVisible);

            // So does a disk-full warning, after its message has ended.
            Raise(engine, Notice("d", ErrorCodes.DiskFull, WarningSeverity.Error, "The disk is full", DateTimeOffset.UtcNow));
            time.Advance(OverlayViewModel.MessageDuration);
            Assert.True(window.IsVisible);

            Dismiss(engine, "d");
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void AnotherWindowKeepsTheForegroundAndFocus() => Sta.Run(() =>
    {
        var box = new TextBox();
        var other = new Window { Title = "Overlay focus helper", Width = 300, Height = 200, Content = box };
        var engine = new FakeEngineSession();
        var overlay = new OverlayWindow(new OverlayViewModel(engine, action => action()));
        try
        {
            other.Show();
            other.Activate();
            box.Focus();
            Sta.Pump(TimeSpan.FromMilliseconds(200));
            var hwnd = new WindowInteropHelper(other).Handle;
            Assert.Equal(hwnd, OverlayWindowStyle.ForegroundWindow());

            foreach (var state in new[] { Buffering, Buffering with { Recording = RecordingState.Running }, CaptureState.Initial, Buffering })
            {
                Shift(engine, state);
                Sta.Pump(TimeSpan.FromMilliseconds(100));
                Assert.Equal(hwnd, OverlayWindowStyle.ForegroundWindow());
                Assert.Same(box, Keyboard.FocusedElement);
            }

            Assert.True(overlay.IsVisible);
        }
        finally
        {
            overlay.Close();
            other.Close();
        }
    });

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void AScreenCopyShowsNoneOfTheOverlay() => Sta.Run(() =>
    {
        var (x, y, size) = Monitors.TopRight(Monitors.ForProcess(null), OverlayWindow.DotSizeDip, OverlayWindow.InsetDip);
        var green = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
            Background = Brushes.Lime,
        };
        var engine = new FakeEngineSession();
        var overlay = new OverlayWindow(new OverlayViewModel(engine, action => action()));
        try
        {
            green.Show();
            const int Margin = 20;
            OverlayWindowStyle.ShowWithoutActivation(
                new WindowInteropHelper(green).Handle, x - Margin, y - Margin, size + (2 * Margin), size + (2 * Margin));
            Shift(engine, Buffering with { Recording = RecordingState.Running });
            Sta.Pump(TimeSpan.FromMilliseconds(500));
            Assert.True(overlay.IsVisible);

            using var bitmap = new System.Drawing.Bitmap(size, size);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(size, size));
            }

            var greens = 0;
            for (var px = 0; px < size; px++)
            {
                for (var py = 0; py < size; py++)
                {
                    var c = bitmap.GetPixel(px, py);
                    Assert.False(c.R > 150 && c.G < 100 && c.B < 100, $"A red pixel at {px},{py}: {c}.");
                    greens += c.G > 150 && c.R < 100 ? 1 : 0;
                }
            }

            // The helper sits under the Overlay, so the copy did see that part of the screen.
            Assert.True(greens > size * size / 2, $"Only {greens} of {size * size} pixels were green.");
        }
        finally
        {
            overlay.Close();
            green.Close();
        }
    });

    private static OverlayDot After(FakeEngineSession engine, OverlayViewModel model, CaptureState capture)
    {
        Shift(engine, capture);
        return model.Dot;
    }

    private static void Shift(FakeEngineSession engine, CaptureState capture)
    {
        engine.State = engine.State! with { Capture = capture };
        engine.RaiseChanged();
    }

    private static void SetOverlayDot(FakeEngineSession engine, OverlayDotMode mode)
    {
        engine.State = engine.State! with
        {
            Settings = SettingsDocument.Empty with { Global = SettingsMaps.EmptyValues.Add(Keys.OverlayDot.Name, Keys.OverlayDot.ToJson(mode)) },
        };
        engine.RaiseChanged();
    }

    private static Warning Notice(string id, string code, WarningSeverity severity, string message, DateTimeOffset at) =>
        new(id, code, severity, "capture", message, at, Overlay: true, Sound: null, Dismissable: true);

    private static void Raise(FakeEngineSession engine, params Warning[] warnings)
    {
        engine.State = engine.State! with { Warnings = [.. engine.State.Warnings, .. warnings] };
        engine.RaiseChanged();
    }

    private static void Dismiss(FakeEngineSession engine, string id)
    {
        engine.State = engine.State! with { Warnings = [.. engine.State.Warnings.Where(w => w.Id != id)] };
        engine.RaiseChanged();
    }

    /// <summary>A clock that moves only when told, firing the one-shot timers that come due.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }

        /// <param name="clockLags">Fires the timers due by <c>by + clockLags</c>, as a timer that runs a little early does.</param>
        public void Advance(TimeSpan by, TimeSpan clockLags = default)
        {
            _now += by;
            foreach (var due in _timers.Where(t => t.DueAt <= _now + clockLags).ToList())
            {
                _timers.Remove(due);
                due.Fire();
            }
        }

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
        {
            public DateTimeOffset DueAt { get; } = dueAt;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose() => owner._timers.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
