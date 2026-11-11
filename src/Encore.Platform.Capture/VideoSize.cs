namespace Encore.Platform.Capture;

/// <summary>A frame size in pixels.</summary>
public readonly record struct VideoSize(int Width, int Height)
{
    /// <summary>The encoder size for a content size: rounded down to even, as 4:2:0 video needs, and never below 2×2.</summary>
    public static VideoSize EvenFloor(int width, int height) =>
        new(Math.Max(2, width & ~1), Math.Max(2, height & ~1));

    /// <summary>The overlap of two sizes, which is what a resized frame can copy.</summary>
    public VideoSize Intersect(VideoSize other) =>
        new(Math.Min(Width, other.Width), Math.Min(Height, other.Height));

    public override string ToString() => $"{Width}x{Height}";
}
