using Encore.Contracts.Settings;

namespace Encore.Core.Output;

/// <summary>
/// AD-26's held size change (story 12.2): watches a game's captured content size against its chain's
/// output size, frame by frame, and says when Rewind should move to a new size. A new size
/// (<see cref="SourceOutputSize"/>'s, for the chain's codec) must hold for <see cref="Hold"/> of frame
/// time. A frame under 640×360 (a minimised or launcher window) is ignored: it neither starts nor
/// resets a candidate. While a Recording is attached nothing fires (R12: a Recording is never split
/// or resized); a candidate still held fires at the first frame after it ends. A fire clears the
/// candidate: the caller stops observing while its move runs, and a move that didn't happen needs the
/// size to hold again. Not thread-safe: one capture thread.
/// </summary>
public sealed class SourceSizeWatch(long qpcFrequency)
{
    /// <summary>How long a new size must hold before the chain moves.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(3);

    private readonly long _holdQpc = (long)(Hold.TotalSeconds * qpcFrequency);
    private (int Width, int Height)? _candidate;
    private long _since;

    /// <summary>
    /// One frame at <paramref name="qpc"/> of <paramref name="contentWidth"/> × <paramref name="contentHeight"/>,
    /// with the chain at <paramref name="chainWidth"/> × <paramref name="chainHeight"/> under
    /// <paramref name="codec"/>. Returns the size to move to, once, or null.
    /// </summary>
    public (int Width, int Height)? Observe(long qpc, int contentWidth, int contentHeight, int chainWidth, int chainHeight, VideoCodec codec, bool recording)
    {
        if (SourceOutputSize.For(contentWidth, contentHeight, codec) is not { } target)
        {
            return null;
        }

        if (target == (chainWidth, chainHeight))
        {
            _candidate = null;
            return null;
        }

        if (_candidate != target)
        {
            _candidate = target;
            _since = qpc;
        }

        if (recording || qpc - _since < _holdQpc)
        {
            return null;
        }

        _candidate = null;
        return target;
    }
}
