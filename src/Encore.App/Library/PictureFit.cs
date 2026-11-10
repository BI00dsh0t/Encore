namespace Encore.App.Library;

/// <summary>The size of the texture a video is drawn into, for the area the player page gives it.</summary>
internal static class PictureFit
{
    /// <summary>The video's aspect, never larger than the video or the area, with even sides; 2 × 2 for a video with no size.</summary>
    public static (int Width, int Height) Size(int videoWidth, int videoHeight, double areaWidth, double areaHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0)
        {
            return (2, 2);
        }

        var scale = Math.Min(1, Math.Min(Math.Max(areaWidth, 2) / videoWidth, Math.Max(areaHeight, 2) / videoHeight));
        return (Even(videoWidth * scale), Even(videoHeight * scale));
    }

    private static int Even(double value) => Math.Max(2, (int)value & ~1);
}
