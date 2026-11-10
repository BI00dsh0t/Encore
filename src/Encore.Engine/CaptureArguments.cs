using Encore.Contracts.Settings;

namespace Encore.Engine;

/// <summary>The <c>debug-capture</c> values read so far, starting at their defaults.</summary>
internal sealed class CaptureArguments
{
    public string? Window { get; set; }

    public nint? Hwnd { get; set; }

    public IReadOnlyList<string>? Monitors { get; set; }

    public bool MonitorDuplication { get; set; }

    public int Seconds { get; set; } = DebugCaptureCommand.DefaultSeconds;

    public string? Output { get; set; }

    public string? Stats { get; set; }

    public string? Instance { get; set; }

    public string? OutputDevice { get; set; }

    public string? InputDevice { get; set; }

    public bool TrackLayout { get; set; }

    public int? GpuPriority { get; set; }

    public CaptureStages Stages { get; set; } = CaptureStages.Full;

    public string NvencPreset { get; set; } = DebugCaptureCommand.DefaultNvencPreset;

    public string? NvencTune { get; set; }

    public ColorPath Color { get; set; } = ColorPath.X1;

    public bool EncodeThread { get; set; }

    public string? Encoder { get; set; }

    public VideoCodec Codec { get; set; } = VideoCodec.H264;

    public DebugCaptureSize? Size { get; set; }

    public int Fps { get; set; } = DebugCaptureCommand.DefaultFps;

    public int BitrateKbps { get; set; } = DebugCaptureCommand.DefaultBitrateKbps;

    public bool MatchSource { get; set; }

    public DebugCaptureCommand ToCommand() => new(
        Window,
        Hwnd,
        Seconds,
        Output,
        OutputDevice,
        InputDevice,
        Stats,
        GpuPriority,
        Stages,
        NvencPreset,
        NvencTune,
        Color,
        EncodeThread,
        Codec,
        Size,
        Fps,
        BitrateKbps,
        Encoder,
        TrackLayout,
        Instance,
        Monitors,
        MonitorDuplication,
        MatchSource);
}
