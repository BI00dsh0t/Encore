using Microsoft.Extensions.Logging.Abstractions;
using Encore.Media;

namespace Encore.Platform.Capture.Tests;

/// <summary>The composite source's start and teardown, with fake screens and a fake canvas (no GPU).</summary>
public sealed class CompositeCaptureTests
{
    private static readonly MonitorPlacement[] TwoScreens =
    [
        new(new MonitorInfo(1, @"\\.\DISPLAY1", true), 0, 0, 2560, 1440, 240),
        new(new MonitorInfo(2, @"\\.\DISPLAY2", false), 2560, 0, 3840, 2160, 144),
    ];

    /// <summary>A bug that bit: a screen that failed to start left the slot clock unstarted, and Dispose hid the real error.</summary>
    [Fact]
    public void AScreenThatFailsToStartFailsWithItsOwnErrorAndDisposeStopsTheOthers()
    {
        var made = new List<FakeSource>();
        var canvas = new FakeCanvas();
        var composite = new CompositeCapture(
            TwoScreens,
            fps: 60,
            (source, _) =>
            {
                var fake = new FakeSource(fails: source.Index == 1);
                made.Add(fake);
                return fake;
            },
            _ => canvas,
            (_, _, _) => { },
            NullLogger.Instance);

        var failure = Assert.Throws<InvalidOperationException>(composite.Start);
        Assert.Equal("the screen's capture couldn't start", failure.Message);

        composite.Dispose();
        Assert.All(made, f => Assert.True(f.Disposed));
        Assert.True(canvas.Disposed);
    }

    [Fact]
    public void ADropAfterTheTeardownDoesNothing()
    {
        var canvas = new FakeCanvas();
        var composite = new CompositeCapture(TwoScreens, fps: 60, (_, _) => new FakeSource(fails: false), _ => canvas, (_, _, _) => { }, NullLogger.Instance);
        composite.Start();
        Assert.True(composite.Drop(@"\\.\DISPLAY2"));
        Assert.Single(canvas.Cleared);

        composite.Dispose();

        Assert.False(composite.Drop(@"\\.\DISPLAY1"));
        Assert.Single(canvas.Cleared);
    }

    private sealed class FakeSource(bool fails) : ICaptureSource
    {
        public event Action? Closed;

        public event Action<Exception>? Failed;

        public bool Disposed { get; private set; }

        public void Start()
        {
            if (fails)
            {
                throw new InvalidOperationException("the screen's capture couldn't start");
            }
        }

        public void SetFps(int fps)
        {
        }

        public void Dispose()
        {
            Disposed = true;
            _ = Closed;
            _ = Failed;
        }
    }

    private sealed class FakeCanvas : CompositeCapture.ICanvasTarget
    {
        public List<CanvasRect> Cleared { get; } = [];

        public bool Disposed { get; private set; }

        public void Blit(nint texture, int contentWidth, int contentHeight, CanvasRect area)
        {
        }

        public void Clear(CanvasRect area) => Cleared.Add(area);

        public void Present(long qpc)
        {
        }

        public void Dispose() => Disposed = true;
    }
}
