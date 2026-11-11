using Encore.Contracts.Settings;

namespace Encore.Core.Output;

/// <summary>Watches a game's captured size and says when Rewind should move to a new size. docs/code-notes.md.</summary>
public sealed class SourceSizeWatch(long qpcFrequency)
{
    /// <summary>How long a new size must hold before the chain moves.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(3);

    private readonly long _holdQpc = (long)(Hold.TotalSeconds * qpcFrequency);
    private (int Width, int Height)? _candidate;
    private long _since;

    /// <summary>Observes one frame's size against the chain's; returns the size to move to, once, or null.</summary>
    public (int Width, int Height)? Observe(
        long qpc,
        int contentWidth,
        int contentHeight,
        int chainWidth,
        int chainHeight,
        VideoCodec codec,
        bool recording)
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
