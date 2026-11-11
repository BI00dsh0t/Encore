using Encore.Core.Replay;

namespace Encore.Engine.Sessions;

/// <summary>The engine's own session options: where the output goes, callbacks and experiments; none is part of the spec.</summary>
internal sealed record CaptureSessionOptions
{
    /// <summary>Rewind: the encoders write into a ring instead of a file, and each save writes a Clip from it.</summary>
    public bool Replay { get; init; }

    /// <summary>Where a Disk-mode ring's file goes when the snapshot's <c>ringPath</c> is not set; null for none.</summary>
    public string? RingFolder { get; init; }

    /// <summary>The engine's RAM margin, shared by every ring of every session; null for none.</summary>
    public MemoryMargin? Margin { get; init; }

    /// <summary>The folder a file is allocated in when neither <see cref="OutputPath"/> nor the snapshot's Output Folder is set.</summary>
    public required string OutputFolder { get; init; }

    /// <summary>The exact file to write, or null to allocate one per session.</summary>
    public string? OutputPath { get; init; }

    /// <summary>When set, the session records into it and writes the report to <see cref="StatsPath"/>.</summary>
    public CaptureStatsCollector? Stats { get; init; }

    public string? StatsPath { get; init; }

    /// <summary>The run length the stats report records.</summary>
    public int RequestedSeconds { get; init; }

    public int? GpuPriority { get; init; }

    public CaptureStages Stages { get; init; } = CaptureStages.Full;

    public string NvencPreset { get; init; } = DebugCaptureCommand.DefaultNvencPreset;

    public string? NvencTune { get; init; }

    public ColorPath Color { get; init; } = ColorPath.X1;

    /// <summary>An FFmpeg encoder every session uses whatever the codec, bypassing the probe; for <c>--encoder</c> and tests.</summary>
    public string? Encoder { get; init; }

    /// <summary>Called once, on the capture thread, with the anchor QPC once the first frame has opened the output.</summary>
    public Action<long>? Anchored { get; init; }

    /// <summary>Called once, on the opening thread, when a Monitor session falls back to Desktop Duplication.</summary>
    public Action<string>? CaptureFallback { get; init; }

    /// <summary>When set, a game session whose window closes keeps running and posts Closed, so it can be retargeted.</summary>
    public bool FollowWindow { get; init; }

    /// <summary>Called on a background thread when a chain switch left a gap of more than one frame before the next Clip.</summary>
    public Action<string>? ResetGap { get; init; }

    /// <summary>Dev only: a file session captures these monitors composited into one canvas instead of a window.</summary>
    public IReadOnlyList<string>? CompositeMonitors { get; init; }

    /// <summary>Dev only: <see cref="CompositeMonitors"/> captures every monitor through Desktop Duplication.</summary>
    public bool CompositeDuplication { get; init; }

    /// <summary>Called once, on the opening thread, with a warning code and message for the Monitor session.</summary>
    public Action<string, string>? Warn { get; init; }
}
