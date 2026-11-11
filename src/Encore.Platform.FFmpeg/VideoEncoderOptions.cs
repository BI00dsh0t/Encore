using Encore.Contracts.Settings;

namespace Encore.Platform.FFmpeg;

/// <summary>What a <see cref="VideoEncoder"/> encodes with; Preset and Tune apply to NVENC only, and the GOP is one second.</summary>
public sealed record VideoEncoderOptions(
    string Preset = VideoEncoderOptions.DefaultPreset,
    string? Tune = null,
    string Encoder = VideoEncoderOptions.DefaultEncoder,
    VideoCodec Codec = VideoCodec.H264,
    int Fps = VideoEncoderOptions.DefaultFps,
    int BitrateKbps = VideoEncoderOptions.DefaultBitrateKbps,
    RateControl RateControl = RateControl.Cbr)
{
    public const string DefaultPreset = "p5";
    public const string DefaultEncoder = "h264_nvenc";
    public const int DefaultFps = 60;
    public const int DefaultBitrateKbps = 20_000;

    public static VideoEncoderOptions Default { get; } = new();
}
