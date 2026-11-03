using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Overlay;
using ReplayBuffer.Contracts.Messages;
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
    public void TheWindowIsTopmostClickThroughNeverActivatedAndExcludedFromCapture() => Sta.Run(() =>
    {
        var engine = new FakeEngineSession();
        var window = new OverlayWindow(new OverlayViewModel(engine, action => action()));
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
            Pump(TimeSpan.FromMilliseconds(200));
            var hwnd = new WindowInteropHelper(other).Handle;
            Assert.Equal(hwnd, OverlayWindowStyle.ForegroundWindow());

            foreach (var state in new[] { Buffering, Buffering with { Recording = RecordingState.Running }, CaptureState.Initial, Buffering })
            {
                Shift(engine, state);
                Pump(TimeSpan.FromMilliseconds(100));
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
            Pump(TimeSpan.FromMilliseconds(500));
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

    // Runs the dispatcher for a while, so windows show, render and settle.
    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }
}
