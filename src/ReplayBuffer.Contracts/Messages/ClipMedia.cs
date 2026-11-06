using System.Text.Json.Serialization;

namespace ReplayBuffer.Contracts.Messages;

// AD-28: these messages belong only to the short-lived worker, never the live IPC catalog.
public sealed record ClipSource(string Path, long Length, long ModifiedUtcTicks);
public sealed record ClipAudioTrack(int StreamIndex, string Name, int SampleRate, int Channels);
public sealed record ClipMetadata(double Duration, int Width, int Height, double FrameRate, ClipAudioTrack[] Tracks, double Origin = 0);
public sealed record ClipRange(double Start, double End, string Alignment = "videoFrames");
public sealed record ClipWaveform(double Start, double End, int StreamIndex, float[] Peaks);
public sealed record ClipMediaRequest(
    string Operation, ClipSource Source, double Start = 0, double End = 0,
    int Bins = 1000, int[]? Tracks = null, bool Mix = false,
    string Format = "mp4", string? Destination = null);
public sealed record ClipMediaResponse(
    string Kind, ClipMetadata? Metadata = null, ClipRange? Range = null,
    ClipWaveform[]? Waveforms = null, double Progress = 0,
    string? Output = null, string? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClipMediaRequest))]
[JsonSerializable(typeof(ClipMediaResponse))]
public sealed partial class ClipMediaJsonContext : JsonSerializerContext;
