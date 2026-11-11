using Encore.Contracts.Settings;

namespace Encore.Engine.Sessions;

/// <summary>Which encoder a session uses for each codec.</summary>
internal interface IVideoEncoderCatalog
{
    /// <summary>The FFmpeg encoder the probe chose for a codec, or null when none works; a probe that throws is retried.</summary>
    string? EncoderFor(VideoCodec codec);
}
