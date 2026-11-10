namespace Encore.Media;

/// <summary>Where scaled content sits inside the output: a rectangle in output pixels.</summary>
public readonly record struct LetterboxRect(int X, int Y, int Width, int Height)
{
    /// <summary>True when the rectangle covers the whole of a <paramref name="width"/>×<paramref name="height"/> output.</summary>
    public bool Covers(int width, int height) => X == 0 && Y == 0 && Width == width && Height == height;
}

/// <summary>
/// The fixed-output fit (R20): content scaled to the largest size that fits the output with its
/// aspect ratio kept, centred, black bars around it. Every edge is even, so 4:2:0 chroma never
/// straddles a bar; a bar that can't split evenly is the larger one at the bottom or right.
/// </summary>
public static class Letterbox
{
    public static LetterboxRect Fit(int contentWidth, int contentHeight, int outputWidth, int outputHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);
        if (contentWidth <= 0 || contentHeight <= 0)
        {
            return new LetterboxRect(0, 0, outputWidth, outputHeight);
        }

        int width, height;
        if ((long)contentWidth * outputHeight >= (long)contentHeight * outputWidth)
        {
            // As wide as the output, or wider: bars top and bottom.
            width = outputWidth;
            height = Math.Clamp(EvenRound((long)contentHeight * outputWidth, contentWidth), 2, outputHeight);
        }
        else
        {
            // Narrower: bars left and right.
            height = outputHeight;
            width = Math.Clamp(EvenRound((long)contentWidth * outputHeight, contentHeight), 2, outputWidth);
        }

        return new LetterboxRect(((outputWidth - width) / 2) & ~1, ((outputHeight - height) / 2) & ~1, width, height);
    }

    /// <summary><paramref name="numerator"/> / <paramref name="denominator"/>, rounded to the nearest even integer.</summary>
    private static int EvenRound(long numerator, long denominator) =>
        (int)((numerator + denominator) / (2 * denominator) * 2);
}
