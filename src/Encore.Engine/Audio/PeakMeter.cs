using Encore.Media;

namespace Encore.Engine.Audio;

/// <summary>A Track's meter: the largest sample since the last <see cref="Take"/>; device threads write it without blocking.</summary>
internal sealed class PeakMeter : IAudioSampleSink
{
    private int _bits;

    public void Write(ReadOnlySpan<float> interleaved)
    {
        var peak = 0f;
        foreach (var sample in interleaved)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        if (peak <= 0)
        {
            return;
        }

        var seen = Volatile.Read(ref _bits);
        while (BitConverter.Int32BitsToSingle(seen) < peak)
        {
            var prior = Interlocked.CompareExchange(ref _bits, BitConverter.SingleToInt32Bits(peak), seen);
            if (prior == seen)
            {
                return;
            }

            seen = prior;
        }
    }

    public void WriteSilence(long frames)
    {
    }

    /// <summary>The peak since the last take, resetting it to 0.</summary>
    public float Take() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _bits, 0));
}
