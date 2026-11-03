namespace ReplayBuffer.Bench;

/// <summary>
/// Finds the marker window's flashes in the decoded video: each frame is reduced to its mean
/// luma (0–255, from a 64×36 gray decode), and a flash starts at a frame above the threshold whose
/// previous frame was not. The threshold is halfway between the median frame (dark: the marker is
/// black 95 % of the time) and the brightest, so it adapts to letterboxing, the capture border and
/// the encoder's range. With too little contrast there are no flashes at all.
/// </summary>
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
