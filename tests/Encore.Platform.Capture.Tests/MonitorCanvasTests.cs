using Microsoft.Extensions.Logging.Abstractions;

namespace Encore.Platform.Capture.Tests;

/// <summary>The canvas bounding box from placements, and the GPU cost of one monitor's blit into the canvas.</summary>
public sealed class MonitorCanvasTests
{
    [Fact]
    public void TheCanvasIsTheMonitorsBoundingBox()
    {
        // The builder's PC: 2560x1440 primary at (0,0), 3840x2160 at (2560,0).
        var monitor = new MonitorInfo(1, @"\\.\DISPLAY1", true);
        var placements = new[]
        {
            new MonitorPlacement(monitor, 0, 0, 2560, 1440, 240),
            new MonitorPlacement(monitor with { DeviceName = @"\\.\DISPLAY2", IsPrimary = false }, 2560, 0, 3840, 2160, 144),
        };

        var (native, left, top) = MonitorCanvas.Bounds(placements);

        Assert.Equal((new VideoSize(6400, 2160), 0, 0), (native, left, top));
        Assert.Equal(new VideoSize(4096, 1382), MonitorCanvas.Fit(native, 4096));
    }

    /// <summary>The blit's GPU time, written to the test output; a measurement, so it's Heavy.</summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void ABlitOfEachMonitorIntoTheCanvasCostsMicroseconds()
    {
        var primary = MonitorFinder.Find(null);
        using var device = D3D11Device.CreateForMonitor(primary.Handle, NullLogger.Instance);
        var canvas = device.CreateTexture(new VideoSize(6400, 2160));
        var qhd = device.CreateTexture(new VideoSize(2560, 1440));
        var uhd = device.CreateTexture(new VideoSize(3840, 2160));
        try
        {
            device.TimeCopies(qhd, canvas, 0, 0, 10); // warm-up
            var qhdUs = device.TimeCopies(qhd, canvas, 0, 0, 600);
            var uhdUs = device.TimeCopies(uhd, canvas, 2560, 0, 600);

            TestContext.Current.TestOutputHelper?.WriteLine($"Blit 2560x1440: {qhdUs:0.0} us; 3840x2160: {uhdUs:0.0} us (GPU time per copy)");
            Assert.InRange(qhdUs!.Value, 0.1, 5000);
            Assert.InRange(uhdUs!.Value, 0.1, 5000);
        }
        finally
        {
            Textures.Release(uhd);
            Textures.Release(qhd);
            Textures.Release(canvas);
        }
    }
}
