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

/// <summary>
/// <c>capture.stateChanged</c>, one of the two shapes the Overlay and status line render (AD-3).
/// The SessionOrchestrator is its only producer (AD-19).
/// </summary>
/// <param name="ClipsWriting">How many Clips are being written right now; JSON without it reads as 0 (AD-22, additive).</param>
public sealed record CaptureState(
    ReplayState Replay,
    RecordingState Recording,
    RecordingState MonitorRecording,
    bool MicMuted,
    DateTimeOffset? CooldownUntil,
    int ClipsWriting = 0)
{
    /// <summary>Nothing running, and the mic muted, as AD-5 restores it with no saved state.</summary>
    public static CaptureState Initial { get; } = new(ReplayState.Off, RecordingState.Off, RecordingState.Off, MicMuted: true, CooldownUntil: null);
}

/// <summary><c>clip.saved</c>: a Clip was written to <see cref="Path"/> (R4). The SessionOrchestrator is its only producer (AD-19).</summary>
public sealed record ClipSaved(string Path);

/// <summary>
/// <c>clip.saving</c>: a <c>save</c> press was accepted at <see cref="At"/> and its Clip is on its
/// way (R6); <c>clip.saved</c> or <c>clip.failed</c> follows. The SessionOrchestrator is its only
/// producer (AD-19).
/// </summary>
public sealed record ClipSaving(DateTimeOffset At);

/// <summary>
/// <c>clip.failed</c>: a save made no Clip (R4); <see cref="Reason"/> is an error code, e.g.
/// <see cref="ErrorCodes.ClipTooShort"/>. The SessionOrchestrator is its only producer (AD-19).
/// </summary>
public sealed record ClipFailed(string Reason);

/// <summary>A codec this PC can encode, and the FFmpeg encoder the engine's probe chose for it (R2, AD-14).</summary>
/// <param name="Encoder">The FFmpeg encoder name, e.g. <c>h264_nvenc</c>, or <c>libx264</c> when no hardware H.264 encoder works.</param>
/// <param name="MayNotPlayEverywhere">True for HEVC and AV1, which some players and sites can't decode.</param>
public sealed record CodecOffer(VideoCodec Codec, string Encoder, bool MayNotPlayEverywhere);

/// <summary>An output resolution the Output Settings offer.</summary>
public sealed record ResolutionOption(int Width, int Height);

/// <summary>
/// <c>capture.videoOptions</c>: what the <c>video</c> key can be set to on this PC. The engine probes
/// the hardware encoders once at startup; until then <see cref="Probed"/> is false and no codec is
/// offered. The resolutions and <see cref="MaxFps"/> are what's offered: the key itself accepts any
/// even size up to <see cref="VideoSettings.MaxDimension"/>.
/// </summary>
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

/// <summary>
/// <c>estimate.changed</c>: what the Global Buffer Window costs in RAM or disk (R2), from the Global
/// <c>video</c>, <c>bufferWindow</c>, <c>replayStorage</c>, <c>ringPath</c> and Track count.
/// </summary>
/// <param name="Storage">Where the ring lives, so whether <see cref="Bytes"/> is RAM or disk.</param>
/// <param name="Bytes">The ring's bytes at worst for the Global settings.</param>
/// <param name="WorstCase">True for VBR, whose bytes are counted at 1.5 times its bitrate.</param>
/// <param name="FreeBytes">The free RAM for Memory, the ring folder drive's free space for Disk; null when unknown.</param>
/// <param name="ExceedsFree">True when <see cref="Bytes"/> is over <see cref="FreeBytes"/>; false when that is unknown.</param>
public sealed record StorageEstimate(ReplayStorage Storage, long Bytes, bool WorstCase, long? FreeBytes, bool ExceedsFree);

/// <summary>Capture messages: Rewind, the capture session and the shared capture state.</summary>
public static class CaptureMessages
{
    public static EventType<CaptureState> StateChanged { get; } = new(
        "capture.stateChanged",
        CaptureJsonContext.Default.CaptureState,
        "capture",
        (snapshot, state) => snapshot with { Capture = state });

    /// <summary>The encoder probe's result (R2, AD-14), published once after startup.</summary>
    public static EventType<VideoOptions> VideoOptionsChanged { get; } = new(
        "capture.videoOptions",
        CaptureJsonContext.Default.VideoOptions,
        "videoOptions",
        (snapshot, options) => snapshot with { VideoOptions = options });

    /// <summary>A Clip was saved (R4); the snapshot keeps the last one.</summary>
    public static EventType<ClipSaved> Saved { get; } = new(
        "clip.saved",
        CaptureJsonContext.Default.ClipSaved,
        "lastClip",
        (snapshot, saved) => snapshot with { LastClip = saved });

    /// <summary>A save press was accepted and its Clip is being made (R6); the snapshot keeps the last one.</summary>
    public static EventType<ClipSaving> Saving { get; } = new(
        "clip.saving",
        CaptureJsonContext.Default.ClipSaving,
        "lastClipSaving",
        (snapshot, saving) => snapshot with { LastClipSaving = saving });

    /// <summary>A save made no Clip (R4); the snapshot keeps the last failure.</summary>
    public static EventType<ClipFailed> Failed { get; } = new(
        "clip.failed",
        CaptureJsonContext.Default.ClipFailed,
        "lastClipFailure",
        (snapshot, failed) => snapshot with { LastClipFailure = failed });

    /// <summary>The Global settings' storage estimate (R2). Coalescing: a connection's queue keeps only the latest.</summary>
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
