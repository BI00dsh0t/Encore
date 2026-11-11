using System.Text.Json;
using System.Text.Json.Serialization;
using Encore.Contracts.Ipc;
using Encore.Contracts.Settings;

namespace Encore.Contracts.Messages;

/// <summary>Rewind's state in <see cref="CaptureState"/>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ReplayState>))]
public enum ReplayState
{
    Off,
    Buffering,
    Draining,
}

/// <summary>A Recording's or Monitor Recording's state in <see cref="CaptureState"/>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<RecordingState>))]
public enum RecordingState
{
    Off,
    Running,
}

/// <summary><c>capture.stateChanged</c>: what the Overlay and status line show; only the SessionOrchestrator produces it.</summary>
public sealed record CaptureState(
    ReplayState Replay,
    RecordingState Recording,
    RecordingState MonitorRecording,
    bool MicMuted,
    DateTimeOffset? CooldownUntil,
    int ClipsWriting = 0)
{
    /// <summary>Nothing running, and the mic muted, which is how it starts with no saved state.</summary>
    public static CaptureState Initial { get; } = new(ReplayState.Off, RecordingState.Off, RecordingState.Off, MicMuted: true, CooldownUntil: null);
}

/// <summary><c>clip.saved</c>: a Clip was written to <see cref="Path"/>. The SessionOrchestrator is its only producer.</summary>
public sealed record ClipSaved(string Path);

/// <summary><c>clip.saving</c>: a save press was accepted at <see cref="At"/>; <c>clip.saved</c> or <c>clip.failed</c> follows.</summary>
public sealed record ClipSaving(DateTimeOffset At);

/// <summary><c>clip.failed</c>: a save made no Clip; <see cref="Reason"/> is an error code such as <c>clip-too-short</c>.</summary>
public sealed record ClipFailed(string Reason);

/// <summary>A codec this PC can encode, with the FFmpeg encoder the probe chose; HEVC and AV1 may not play everywhere.</summary>
public sealed record CodecOffer(VideoCodec Codec, string Encoder, bool MayNotPlayEverywhere);

/// <summary>An output resolution the Output Settings offer.</summary>
public sealed record ResolutionOption(int Width, int Height);

/// <summary><c>capture.videoOptions</c>: what the <c>video</c> key can be set to here; no codec is offered until the probe ran.</summary>
public sealed record VideoOptions(bool Probed, IReadOnlyList<CodecOffer> Codecs, IReadOnlyList<ResolutionOption> Resolutions, int MaxFps)
{
    /// <summary>The resolutions every PC is offered, smallest first.</summary>
    public static IReadOnlyList<ResolutionOption> OfferedResolutions { get; } =
    [
        new(1280, 720),
        new(1920, 1080),
        new(2560, 1080),
        new(2560, 1440),
        new(3440, 1440),
        new(3840, 1600),
        new(3840, 2160),
    ];

    /// <summary>Before the probe: no codecs yet.</summary>
    public static VideoOptions Unprobed { get; } = new(false, [], OfferedResolutions, VideoSettings.MaxFps);

    /// <summary>The probe's result: <paramref name="codecs"/>, with the offered resolutions and the fps ceiling.</summary>
    public static VideoOptions FromProbe(IReadOnlyList<CodecOffer> codecs) => new(true, codecs, OfferedResolutions, VideoSettings.MaxFps);

    /// <summary>Whether an offer of <paramref name="codec"/> is marked as one that may not play everywhere.</summary>
    public static bool MayNotPlayEverywhereFor(VideoCodec codec) => codec != VideoCodec.H264;

    /// <summary>Equal when every field is, the lists in order.</summary>
    public bool Equals(VideoOptions? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (Probed == other.Probed && MaxFps == other.MaxFps
                && Codecs.SequenceEqual(other.Codecs) && Resolutions.SequenceEqual(other.Resolutions)));

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Probed);
        hash.Add(MaxFps);
        foreach (var codec in Codecs)
        {
            hash.Add(codec);
        }

        foreach (var resolution in Resolutions)
        {
            hash.Add(resolution);
        }

        return hash.ToHashCode();
    }
}

/// <summary><c>estimate.changed</c>: what the Global Buffer Window costs in RAM or disk; VBR counts at 1.5 times its bitrate.</summary>
public sealed record StorageEstimate(ReplayStorage Storage, long Bytes, bool WorstCase, long? FreeBytes, bool ExceedsFree);

/// <summary>Capture messages: Rewind, the capture session and the shared capture state.</summary>
public static class CaptureMessages
{
    public static EventType<CaptureState> StateChanged { get; } = new(
        "capture.stateChanged",
        CaptureJsonContext.Default.CaptureState,
        "capture",
        (snapshot, state) => snapshot with { Capture = state });

    /// <summary>The encoder probe's result, published once after startup.</summary>
    public static EventType<VideoOptions> VideoOptionsChanged { get; } = new(
        "capture.videoOptions",
        CaptureJsonContext.Default.VideoOptions,
        "videoOptions",
        (snapshot, options) => snapshot with { VideoOptions = options });

    /// <summary>A Clip was saved; the snapshot keeps the last one.</summary>
    public static EventType<ClipSaved> Saved { get; } = new(
        "clip.saved",
        CaptureJsonContext.Default.ClipSaved,
        "lastClip",
        (snapshot, saved) => snapshot with { LastClip = saved });

    /// <summary>A save press was accepted and its Clip is being made; the snapshot keeps the last one.</summary>
    public static EventType<ClipSaving> Saving { get; } = new(
        "clip.saving",
        CaptureJsonContext.Default.ClipSaving,
        "lastClipSaving",
        (snapshot, saving) => snapshot with { LastClipSaving = saving });

    /// <summary>A save made no Clip; the snapshot keeps the last failure.</summary>
    public static EventType<ClipFailed> Failed { get; } = new(
        "clip.failed",
        CaptureJsonContext.Default.ClipFailed,
        "lastClipFailure",
        (snapshot, failed) => snapshot with { LastClipFailure = failed });

    /// <summary>The Global settings' storage estimate; a connection's queue keeps only the latest.</summary>
    public static EventType<StorageEstimate> EstimateChanged { get; } = new(
        "estimate.changed",
        CaptureJsonContext.Default.StorageEstimate,
        "estimate",
        (snapshot, estimate) => snapshot with { Estimate = estimate },
        coalesces: true);

    public static IReadOnlyList<CommandType> Commands { get; } = [];

    public static IReadOnlyList<EventType> Events { get; } = [StateChanged, VideoOptionsChanged, Saving, Saved, Failed, EstimateChanged];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CaptureState))]
[JsonSerializable(typeof(VideoOptions))]
[JsonSerializable(typeof(ClipSaving))]
[JsonSerializable(typeof(ClipSaved))]
[JsonSerializable(typeof(ClipFailed))]
[JsonSerializable(typeof(StorageEstimate))]
public sealed partial class CaptureJsonContext : JsonSerializerContext;
