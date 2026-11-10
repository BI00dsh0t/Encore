namespace Encore.Platform.Desktop.Tests;

public sealed class MonitorsTests
{
    [Fact]
    public void TopRightIsInsetAndSizedAtTheMonitorsDpi()
    {
        var monitor = new MonitorArea(0, 0, 1920, 1080, Dpi: 144);

        Assert.Equal((1860, 36, 24), Monitors.TopRight(monitor, sizeDip: 16, insetDip: 24));
    }

    [Fact]
    public void WithNoProcessItIsThePrimaryMonitor()
    {
        var monitor = Monitors.ForProcess(null);

        // The primary monitor's top-left corner is the desktop's origin.
        Assert.Equal((0, 0), (monitor.Left, monitor.Top));
        Assert.True(monitor.Right > 0 && monitor.Bottom > 0 && monitor.Dpi >= 96);

        // A process with no visible window (here, none at all) falls back to the same monitor.
        Assert.Equal(monitor, Monitors.ForProcess(int.MaxValue));
    }

    [Fact]
    public void DxgiSaysWhetherThePrimaryMonitorHasOverlayPlanes()
    {
        // The answer depends on the PC (5.8's research: none on the builder's with two monitors); it is known.
        Assert.NotNull(Monitors.OverlayPlanes(null));
    }
}
