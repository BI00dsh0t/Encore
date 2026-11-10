using Encore.Contracts.Settings;
using Encore.Core.Monitors;

namespace Encore.Core.Output;

/// <summary>
/// The output size of a game capture chain under <c>matchSource</c> (story 12.1, R40): the game's own
/// captured size, rounded down to even sides and scaled down, aspect kept, only to fit the codec's
/// largest side (<see cref="MonitorCanvasRule"/>'s constants: H.264 4096 px, HEVC and AV1 8192 px).
/// The codec is never switched for a game. A game under 640×360 (a launcher or a minimised window)
/// never sets the size.
/// </summary>
public static class SourceOutputSize
{
    /// <summary>The smallest captured size that sets the output size.</summary>
    public const int MinWidth = 640;

    public const int MinHeight = 360;

    /// <summary>The output size for a captured <paramref name="width"/> × <paramref name="height"/> under <paramref name="codec"/>; null under 640×360.</summary>
    public static (int Width, int Height)? For(int width, int height, VideoCodec codec) =>
        width < MinWidth || height < MinHeight ? null : MonitorCanvasRule.Fit(width, height, MonitorCanvasRule.MaxSide(codec));
}
