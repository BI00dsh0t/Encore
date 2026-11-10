namespace Encore.Engine.ClipMedia;

/// <summary>Reads FFmpeg's stereo 32-bit float output and hands each frame's louder channel to the caller.</summary>
internal static class StereoPeaks
{
    private const int FrameBytes = 8;

    /// <summary>Calls frame with each frame's peak, not clamped; returns the bytes of a part frame left at the end.</summary>
    public static async Task<int> ReadAsync(Stream stream, Action<float> frame, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var pending = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(pending), cancellationToken).ConfigureAwait(false)) != 0)
        {
            count += pending;
            var complete = count - (count % FrameBytes);
            for (var offset = 0; offset < complete; offset += FrameBytes)
            {
                var left = Math.Abs(BitConverter.ToSingle(buffer, offset));
                var right = Math.Abs(BitConverter.ToSingle(buffer, offset + 4));
                frame(Math.Max(left, right));
            }

            pending = count - complete;
            buffer.AsSpan(complete, pending).CopyTo(buffer);
        }

        return pending;
    }
}
