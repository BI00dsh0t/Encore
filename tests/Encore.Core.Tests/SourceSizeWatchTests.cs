using Encore.Contracts.Settings;
using Encore.Core.Output;

namespace Encore.Core.Tests;

/// <summary>The held size change, on synthetic frame stamps (1 tick = 1 ms).</summary>
public sealed class SourceSizeWatchTests
{
    private const long Qpf = 1000;

    [Fact]
    public void AChangeHeldThreeSecondsFiresOnceWithTheNewSize()
    {
        var watch = new SourceSizeWatch(Qpf);
        var fired = Run(watch, from: 0, to: 3500, 2560, 1440, chain: (1280, 720));

        Assert.Equal([(3000L, (2560, 1440))], fired);

        // Once the chain has moved, the same size never fires.
        Assert.Empty(Run(watch, from: 3600, to: 9000, 2560, 1440, chain: (2560, 1440)));
    }

    [Fact]
    public void AShortChangeATinyWindowAndAMinimiseAndRestoreDontFire()
    {
        var watch = new SourceSizeWatch(Qpf);

        Assert.Empty(Run(watch, from: 0, to: 2900, 2560, 1440, chain: (1280, 720)));
        Assert.Empty(Run(watch, from: 3000, to: 9000, 1280, 720, chain: (1280, 720)));
        Assert.Empty(Run(watch, from: 9100, to: 15000, 600, 300, chain: (1280, 720)));

        // Minimised (160x28) for 5 s, then back at the chain's size: nothing.
        Assert.Empty(Run(watch, from: 15100, to: 20000, 160, 28, chain: (1280, 720)));
        Assert.Empty(Run(watch, from: 20100, to: 26000, 1280, 720, chain: (1280, 720)));
    }

    [Fact]
    public void ARecordingHoldsTheMoveUntilItEnds()
    {
        var watch = new SourceSizeWatch(Qpf);

        Assert.Empty(Run(watch, from: 0, to: 10000, 2560, 1440, chain: (1280, 720), recording: true));
        Assert.Equal([(10100L, (2560, 1440))], Run(watch, from: 10100, to: 10500, 2560, 1440, chain: (1280, 720)));
    }

    /// <summary>A frame every 100 ticks from <paramref name="from"/> to <paramref name="to"/>; the fires, with their stamps.</summary>
    private static List<(long Qpc, (int, int) Size)> Run(SourceSizeWatch watch, long from, long to, int width, int height, (int Width, int Height) chain, bool recording = false)
    {
        var fired = new List<(long, (int, int))>();
        for (var qpc = from; qpc <= to; qpc += 100)
        {
            if (watch.Observe(qpc, width, height, chain.Width, chain.Height, VideoCodec.H264, recording) is { } size)
            {
                fired.Add((qpc, size));
            }
        }

        return fired;
    }
}
