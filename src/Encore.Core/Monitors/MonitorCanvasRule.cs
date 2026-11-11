using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Monitors;

/// <summary>One screen's place in the canvas, in native (virtual-desktop) pixels from the canvas's top-left.</summary>
public sealed record CanvasArea(DisplayMonitor Screen, int X, int Y, int Width, int Height);

/// <summary>A Monitor Recording's canvas: the screens' bounding box, where each sits in it, and the codec and size encoded.</summary>
public sealed record CanvasPlan(
    VideoCodec Codec,
    int Width,
    int Height,
    int NativeWidth,
    int NativeHeight,
    IReadOnlyList<CanvasArea> Areas)
{
    public bool Scaled => Width < NativeWidth || Height < NativeHeight;
}

/// <summary>The canvas rule of a multi-monitor recording, shared by the engine and the screen picker. docs/code-notes.md.</summary>
public static class MonitorCanvasRule
{
    /// <summary>H.264's largest frame side on NVENC, as measured; the encoder probe reports no limit.</summary>
    public const int H264MaxSide = 4096;

    /// <summary>HEVC's and AV1's largest frame side on NVENC, as measured.</summary>
    public const int HevcAv1MaxSide = 8192;

    /// <summary>The largest frame side <paramref name="codec"/> encodes.</summary>
    public static int MaxSide(VideoCodec codec) => codec == VideoCodec.H264 ? H264MaxSide : HevcAv1MaxSide;

    /// <summary>The canvas of the screens for an Output codec; canEncode says which codecs the probe found.</summary>
    public static CanvasPlan Plan(IReadOnlyList<DisplayMonitor> screens, VideoCodec outputCodec, Func<VideoCodec, bool> canEncode)
    {
        ArgumentOutOfRangeException.ThrowIfZero(screens.Count);
        var placed = Place(screens);
        var left = placed.Min(area => area.X);
        var top = placed.Min(area => area.Y);
        var nativeWidth = placed.Max(area => area.X + area.Width) - left;
        var nativeHeight = placed.Max(area => area.Y + area.Height) - top;

        var codec = outputCodec;
        if (codec == VideoCodec.H264 && Math.Max(nativeWidth, nativeHeight) > H264MaxSide)
        {
            if (canEncode(VideoCodec.Hevc))
            {
                codec = VideoCodec.Hevc;
            }
            else if (canEncode(VideoCodec.Av1))
            {
                codec = VideoCodec.Av1;
            }
        }

        var (width, height) = Fit(nativeWidth, nativeHeight, MaxSide(codec));
        return new CanvasPlan(codec, width, height, nativeWidth, nativeHeight,
            [.. placed.Select(area => area with { X = area.X - left, Y = area.Y - top })]);
    }

    /// <summary>A size scaled down, aspect kept, so neither side exceeds maxSide; never up, and both sides are made even.</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxSide)
    {
        var scale = Math.Min(1.0, Math.Min((double)maxSide / width, (double)maxSide / height));

        // The epsilon keeps an exact fit (6400 × 0.64 = 4096) from flooring to 4095.
        return (Even((int)Math.Floor((width * scale) + 1e-6)), Even((int)Math.Floor((height * scale) + 1e-6)));
    }

    private static int Even(int value) => Math.Max(2, value & ~1);

    private static List<CanvasArea> Place(IReadOnlyList<DisplayMonitor> screens)
    {
        var placed = new List<CanvasArea>(screens.Count);
        var nextX = 0;
        foreach (var screen in screens)
        {
            var area = screen is { X: { } x, Y: { } y }
                ? new CanvasArea(screen, x, y, screen.Width, screen.Height)
                : new CanvasArea(screen, nextX, 0, screen.Width, screen.Height);
            placed.Add(area);
            nextX = area.X + area.Width;
        }

        return placed;
    }
}
