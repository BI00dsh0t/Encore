using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>One stream of a Clip: its codec parameters, title, default flag and time base.</summary>
internal sealed unsafe class ClipStream(AVCodecParameters* parameters, string? title, bool isDefault, AVRational timeBase)
{
    public AVCodecParameters* Parameters { get; } = parameters;

    public string? Title { get; } = title;

    public bool IsDefault { get; } = isDefault;

    public AVRational TimeBase { get; } = timeBase;
}
