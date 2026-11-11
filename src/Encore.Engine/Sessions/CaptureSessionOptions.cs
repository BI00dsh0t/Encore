using Encore.Contracts;
using Encore.Core.Replay;

namespace Encore.Engine.Sessions;

/// <summary>The engine's own session options: where the file goes, test seams and experiments; none is part of the spec.</summary>
internal sealed record CaptureSessionOptions
{
    public static readonly TimeSpan DefaultFirstFrameTimeout = TimeSpan.FromSeconds(5);

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

    /// <summary>An FFmpeg encoder that every session uses whatever the codec, bypassing the probe; for tests.</summary>
    public string? Encoder { get; init; }

    /// <summary>Called once, on the capture thread, with the anchor QPC once the first frame has opened the output.</summary>
    public Action<long>? Anchored { get; init; }

    /// <summary>For tests: called on the writer thread just before a Clip is written; a non-null result is thrown.</summary>
    public Func<string, Exception?>? ClipWriteFault { get; init; }

    /// <summary>For tests: called just before a forced restart builds the new chain; a non-null result is thrown there.</summary>
    public Func<Exception?>? RestartFault { get; init; }

    /// <summary>For tests: called at the start of each GPU-reset recovery attempt; a non-null result is thrown there.</summary>
    public Func<Exception?>? RecoveryFault { get; init; }

    /// <summary>For tests: while it returns true each frame is dropped, as on the secure desktop, where WGC delivers none.</summary>
    public Func<bool>? StarveFrames { get; init; }

    /// <summary>How often a session that can recover checks whether its GPU device was removed or reset.</summary>
    public TimeSpan DeviceWatchInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>For tests: called before any WGC capture is created; a non-null result is thrown, as WGC failing.</summary>
    public Func<Exception?>? WgcFault { get; init; }

    /// <summary>Called once, on the opening thread, when a Monitor session falls back to Desktop Duplication.</summary>
    public Action<string>? CaptureFallback { get; init; }

    /// <summary>When set, a game session whose window closes keeps running and posts Closed, so it can be retargeted.</summary>
    public bool FollowWindow { get; init; }

    /// <summary>With no first frame by then, the session posts a <see cref="ErrorCodes.CaptureFailed"/> failure.</summary>
    public TimeSpan FirstFrameTimeout { get; init; } = DefaultFirstFrameTimeout;

    /// <summary>Called on a background thread when a chain switch left a gap of more than one frame before the next Clip.</summary>
    public Action<string>? ResetGap { get; init; }

    /// <summary>Dev only: a file session captures these monitors composited into one canvas instead of a window.</summary>
    public IReadOnlyList<string>? CompositeMonitors { get; init; }

    /// <summary>Dev only: <see cref="CompositeMonitors"/> captures every monitor through Desktop Duplication.</summary>
    public bool CompositeDuplication { get; init; }

    /// <summary>For tests: how long a size move's built chain waits before it arms. Zero in production.</summary>
    public TimeSpan MoveArmDelay { get; init; }

    /// <summary>For tests: the composited screens, by GDI device name, captured through Desktop Duplication instead of WGC.</summary>
    public IReadOnlyCollection<string>? DuplicationScreens { get; init; }

    /// <summary>Called once, on the opening thread, with a warning code and message for the Monitor session.</summary>
    public Action<string, string>? Warn { get; init; }
}
