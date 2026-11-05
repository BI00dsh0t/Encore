namespace ReplayBuffer.Media.Tests;

/// <summary>Story 11.3: the multi-monitor compositor's slot clock and areas, on a fake canvas.</summary>
public sealed class MonitorCompositorTests
{
    private const long Qpf = 10_000_000;
    private static readonly CanvasRect Left = new(0, 0, 2560, 1440);
    private static readonly CanvasRect Right = new(2560, 0, 3840, 2160);

    [Fact]
    public void TwoSourcesDrawIntoTheirPlacesAndThePacerGetsOneCanvasPerSlotWhateverTheirRates()
    {
        var surface = new FakeSurface();
        var compositor = new MonitorCompositor(surface, [Left, Right], Qpf, fps: 60);

        // One second: the left source at 144 fps, the right at 60, interleaved; the clock ticks at 60 fps.
        var events = Enumerable.Range(0, 144).Select(i => (Qpc: i * Qpf / 144, Source: 0))
            .Concat(Enumerable.Range(0, 60).Select(i => (Qpc: (i * Qpf / 60) + 1234, Source: 1)))
            .Concat(Enumerable.Range(0, 60).Select(i => (Qpc: ((i * Qpf + 59) / 60) + 5000, Source: -1)))
            .OrderBy(e => e.Qpc);
        foreach (var (qpc, source) in events)
        {
            if (source < 0)
            {
                compositor.Tick(qpc);
            }
            else
            {
                compositor.OnFrame(source, 0x1000 + source, source == 0 ? 2560 : 3840, source == 0 ? 1440 : 2160, qpc);
            }
        }

        Assert.Equal(204, surface.Blits.Count);
        Assert.Equal(144, surface.Blits.Count(b => b.Area == Left && b.Texture == 0x1000));
        Assert.Equal(60, surface.Blits.Count(b => b.Area == Right && b.Texture == 0x1001));
        Assert.InRange(surface.Presents.Count, 59, 60);
        Assert.True(surface.Presents.Zip(surface.Presents.Skip(1)).All(p => p.Second > p.First), "stamps increase");

        // The frames after the last tick go out at the next slot; then nothing new hands nothing on, and the pacer repeats the last canvas.
        compositor.Tick(2 * Qpf);
        Assert.False(compositor.Tick((2 * Qpf) + (Qpf / 30)));
    }

    [Fact]
    public void ARemovedSourcesPlaceTurnsBlackAndItsLaterFramesAreIgnored()
    {
        var surface = new FakeSurface();
        var compositor = new MonitorCompositor(surface, [Left, Right], Qpf, fps: 60);
        compositor.OnFrame(1, 0x1001, 3840, 2160, 0);

        Assert.Equal(1, compositor.Remove(1));
        Assert.Equal([Right], surface.Cleared);
        compositor.OnFrame(1, 0x1001, 3840, 2160, Qpf / 60);
        Assert.True(compositor.Tick(Qpf / 30));
        Assert.Single(surface.Blits);

        Assert.Equal(0, compositor.Remove(0));
        Assert.Equal(0, compositor.Live);
    }

    private sealed class FakeSurface : ICanvasSurface
    {
        public List<(nint Texture, int Width, int Height, CanvasRect Area)> Blits { get; } = [];

        public List<CanvasRect> Cleared { get; } = [];

        public List<long> Presents { get; } = [];

        public void Blit(nint texture, int contentWidth, int contentHeight, CanvasRect area) => Blits.Add((texture, contentWidth, contentHeight, area));

        public void Clear(CanvasRect area) => Cleared.Add(area);

        public void Present(long qpc) => Presents.Add(qpc);
    }
}
