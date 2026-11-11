using Encore.Contracts.Settings;

namespace Encore.Engine;

/// <summary><c>debug-capture</c>: a test capture of one window; a null Size is the window's size, made even. docs/code-notes.md.</summary>
public sealed record DebugCaptureCommand(
    string? Window,
    nint? Hwnd,
    int Seconds,
    string? Output,
    string? OutputDevice,
    string? InputDevice,
    string? Stats = null,
    int? GpuPriority = null,
    CaptureStages Stages = CaptureStages.Full,
    string NvencPreset = DebugCaptureCommand.DefaultNvencPreset,
    string? NvencTune = null,
    ColorPath Color = ColorPath.X1,
    bool EncodeThread = false,
    VideoCodec Codec = VideoCodec.H264,
    DebugCaptureSize? Size = null,
    int Fps = DebugCaptureCommand.DefaultFps,
    int BitrateKbps = DebugCaptureCommand.DefaultBitrateKbps,
    string? Encoder = null,
    bool TrackLayout = false,
    string? Instance = null,
    IReadOnlyList<string>? Monitors = null,
    bool MonitorDuplication = false,
    bool MatchSource = false) : EngineCommand
{
    public const int DefaultSeconds = 30;

    /// <summary>The tracer's frame rate and bitrate, kept as <c>debug-capture</c>'s defaults.</summary>
    public const int DefaultFps = 60;

    public const int DefaultBitrateKbps = 20_000;

    public const string DefaultNvencPreset = "p5";

    /// <summary>The range <c>IDXGIDevice::SetGPUThreadPriority</c> accepts.</summary>
    public const int MinGpuPriority = -7;

    public const int MaxGpuPriority = 7;
}

/// <summary><c>debug-capture --size</c>: the output size, <c>WxH</c>.</summary>
public sealed record DebugCaptureSize(int Width, int Height);
