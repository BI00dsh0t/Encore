namespace Encore.Bench;

/// <summary>Finds the marker's flashes in the decoded video: a frame's mean luma crossing a threshold that adapts to the file.</summary>
internal static class FlashDetector
{
    /// <summary>The least brightest-minus-median that counts as a flash, in luma levels.</summary>
    public const double MinContrast = 24;

    /// <summary>The mean luma of a gray frame.</summary>
    public static double MeanLuma(ReadOnlySpan<byte> frame)
    {
        long sum = 0;
        foreach (var value in frame)
        {
            sum += value;
        }

        return frame.Length == 0 ? 0 : (double)sum / frame.Length;
    }

    /// <summary>The index of each frame that starts a flash.</summary>
    public static List<int> Detect(IReadOnlyList<double> means)
    {
        var flashes = new List<int>();
        if (means.Count == 0)
        {
            return flashes;
        }

        var sorted = means.Order().ToArray();
        var median = sorted[sorted.Length / 2];
        var max = sorted[^1];
        if (max - median < MinContrast)
        {
            return flashes;
        }

        var threshold = (median + max) / 2;
        for (var i = 0; i < means.Count; i++)
        {
            if (means[i] > threshold && (i == 0 || means[i - 1] <= threshold))
            {
                flashes.Add(i);
            }
        }

        return flashes;
    }
}
