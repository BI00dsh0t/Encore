using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Monitors;

/// <summary>One screen's place in the canvas, in native (virtual-desktop) pixels from the canvas's top-left.</summary>
public sealed record CanvasArea(DisplayMonitor Screen, int X, int Y, int Width, int Height);

/// <summary>
/// A Monitor Recording's canvas (AD-25): the native bounding box of its screens and where each sits in
/// it, and the codec and size it is encoded at. <see cref="Scaled"/> when the encoded size is below the
/// native one.
/// </summary>
public sealed record CanvasPlan(VideoCodec Codec, int Width, int Height, int NativeWidth, int NativeHeight, IReadOnlyList<CanvasArea> Areas)
{
    public bool Scaled => Width < NativeWidth || Height < NativeHeight;
}

/// <summary>
/// The canvas rule of a multi-monitor Monitor Recording (AD-25, spike 11.1), shared by the engine and
/// the screen picker:
/// <list type="bullet">
/// <item>The canvas is the bounding box of the screens at their physical positions and sizes.</item>
/// <item>A canvas wider or taller than H.264's largest side, when H.264 is the Output codec, is encoded
/// with HEVC, else AV1, whichever this PC can encode, at full size; H.264 stays for a single screen or
/// a canvas that fits.</item>
/// <item>The canvas is scaled down, aspect kept, only to fit the codec's largest side (no pixel
/// budget), and both sides are rounded down to even.</item>
/// </list>
/// </summary>
public static class MonitorCanvasRule
{
    /// <summary>H.264's largest frame side on NVENC (measured, spike 11.1; the encoder probe reports no limit).</summary>
    public const int H264MaxSide = 4096;

    /// <summary>HEVC's and AV1's largest frame side on NVENC (measured, spike 11.1).</summary>
    public const int HevcAv1MaxSide = 8192;

    /// <summary>The largest frame side <paramref name="codec"/> encodes.</summary>
    public static int MaxSide(VideoCodec codec) => codec == VideoCodec.H264 ? H264MaxSide : HevcAv1MaxSide;

    /// <summary>
    /// The canvas of <paramref name="screens"/> (at least one) for the Output codec <paramref name="outputCodec"/>.
    /// <paramref name="canEncode"/> says which codecs the encoder probe found. A screen without a
    /// position (from an engine that doesn't report one) is placed right of the previous one, top-aligned.
    /// With no HEVC or AV1 encoder at all, a canvas over 4096 px stays H.264 and is fitted to 4096.
    /// </summary>
    public static CanvasPlan Plan(IReadOnlyList<DisplayMonitor> screens, VideoCodec outputCodec, Func<VideoCodec, bool> canEncode)
    {
        ArgumentOutOfRangeException.ThrowIfZero(screens.Count);
        var placed = Place(screens);
        var left = placed.Min(p => p.X);
        var top = placed.Min(p => p.Y);
        var nativeWidth = placed.Max(p => p.X + p.Width) - left;
        var nativeHeight = placed.Max(p => p.Y + p.Height) - top;

        var codec = outputCodec;
        if (codec == VideoCodec.H264 && Math.Max(nativeWidth, nativeHeight) > H264MaxSide)
        {
            codec = canEncode(VideoCodec.Hevc) ? VideoCodec.Hevc : canEncode(VideoCodec.Av1) ? VideoCodec.Av1 : VideoCodec.H264;
        }

        var (width, height) = Fit(nativeWidth, nativeHeight, MaxSide(codec));
        return new CanvasPlan(codec, width, height, nativeWidth, nativeHeight,
            [.. placed.Select(p => p with { X = p.X - left, Y = p.Y - top })]);
    }

    /// <summary>
    /// <paramref name="width"/> × <paramref name="height"/> scaled down, aspect kept, so neither side
    /// exceeds <paramref name="maxSide"/>; never scaled up; both sides rounded down to even (at least 2).
    /// </summary>
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
            var area = screen is { X: { } x, Y: { } y } ? new CanvasArea(screen, x, y, screen.Width, screen.Height) : new CanvasArea(screen, nextX, 0, screen.Width, screen.Height);
            placed.Add(area);
            nextX = area.X + area.Width;
        }

        return placed;
    }
}
