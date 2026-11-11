namespace Encore.Media;

/// <summary>Where scaled content sits inside the output: a rectangle in output pixels.</summary>
public readonly record struct LetterboxRect(int X, int Y, int Width, int Height)
{
    /// <summary>True when the rectangle covers the whole of a <paramref name="width"/>×<paramref name="height"/> output.</summary>
    public bool Covers(int width, int height) => X == 0 && Y == 0 && Width == width && Height == height;
}
