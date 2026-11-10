namespace Encore.Engine.ClipMedia;

/// <summary>Writes one peak per millisecond of 48 kHz frames to a peak file, then pads it to its length with silence.</summary>
internal sealed class MillisecondPeakWriter(BinaryWriter writer, long count)
{
    private const int FramesPerPeak = 48;

    private long _written;
    private int _frames;
    private float _peak;

    /// <summary>Takes one frame's peak; a value that isn't finite counts as a frame but not as a peak.</summary>
    public void Add(float value)
    {
        if (float.IsFinite(value))
        {
            _peak = Math.Max(_peak, Math.Min(1, value));
        }

        if (++_frames < FramesPerPeak)
        {
            return;
        }

        WritePeak();
        _frames = 0;
        _peak = 0;
    }

    /// <summary>Writes the last part peak, then silence up to the file's length.</summary>
    public void Finish(CancellationToken token)
    {
        if (_frames != 0)
        {
            WritePeak();
        }

        while (_written < count)
        {
            token.ThrowIfCancellationRequested();
            writer.Write(0f);
            _written++;
        }
    }

    // Decoders can run a little past the length; those peaks are dropped.
    private void WritePeak()
    {
        if (_written < count)
        {
            writer.Write(_peak);
            _written++;
        }
    }
}
