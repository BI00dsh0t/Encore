using Encore.Contracts.Settings;
using Encore.Core.Monitors;

namespace Encore.Core.Output;

/// <summary>A game chain's output size when matching the source: the game's own size, even, scaled only to fit the codec.</summary>
public static class SourceOutputSize
{
    /// <summary>The smallest captured size that sets the output size.</summary>
    public const int MinWidth = 640;

    public const int MinHeight = 360;

    /// <summary>The output size for a captured size under a codec; null under 640×360.</summary>
    public static (int Width, int Height)? For(int width, int height, VideoCodec codec) =>
        width < MinWidth || height < MinHeight ? null : MonitorCanvasRule.Fit(width, height, MonitorCanvasRule.MaxSide(codec));
}
