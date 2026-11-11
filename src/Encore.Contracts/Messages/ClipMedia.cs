using System.Text.Json.Serialization;

namespace Encore.Contracts.Messages;

// These messages belong only to the short-lived worker, never the live IPC catalog.
/// <summary>The file a clip media request reads, as the App saw it: path, size and modified time.</summary>
public sealed record ClipSource(string Path, long Length, long ModifiedUtcTicks);
/// <summary>One audio stream of a source: its stream index, name and format.</summary>
public sealed record ClipAudioTrack(int StreamIndex, string Name, int SampleRate, int Channels);
/// <summary>What a probe reads of a source: length, picture size, frame rate and audio Tracks.</summary>
public sealed record ClipMetadata(double Duration, int Width, int Height, double FrameRate, ClipAudioTrack[] Tracks, double Origin = 0);
/// <summary>A start and end in seconds, snapped to video frames or to audio samples.</summary>
public sealed record ClipRange(double Start, double End, string Alignment = "videoFrames");
/// <summary>The peaks of one audio stream over a range, one value per bin.</summary>
public sealed record ClipWaveform(double Start, double End, int StreamIndex, float[] Peaks);
/// <summary>One level of a peak file: its file name, how many milliseconds a peak covers, and the peak count.</summary>
public sealed record ClipPeakLevel(string FileName, long MillisecondsPerPeak, long Count);
/// <summary>The peak files of one audio stream, from finest level to coarsest.</summary>
public sealed record ClipPeakTrack(int StreamIndex, ClipPeakLevel[] Levels);
/// <summary>The index of a source's waveform cache: its version, source, length and each stream's peak files.</summary>
public sealed record ClipWaveformDescriptor(int Version, ClipSource Source, double Duration, ClipPeakTrack[] Tracks);
/// <summary>One thumbnail file and the source time it shows.</summary>
public sealed record ClipThumbnail(double Time, string FileName);
/// <summary>The index of a source's thumbnail cache: its version, source, length and frames.</summary>
public sealed record ClipThumbnailDescriptor(int Version, ClipSource Source, double Duration, ClipThumbnail[] Frames);
/// <summary>One request to the clip media worker: an operation on a source, with the fields that operation reads.</summary>
public sealed record ClipMediaRequest(
    string Operation, ClipSource Source, double Start = 0, double End = 0,
    int Bins = 1000, int[]? Tracks = null, bool Mix = false,
    string Format = "mp4", string? Destination = null);
/// <summary>The worker's answer: a Kind, and the fields that kind fills in.</summary>
public sealed record ClipMediaResponse(
    string Kind, ClipMetadata? Metadata = null, ClipRange? Range = null,
    ClipWaveform[]? Waveforms = null, double Progress = 0,
    string? Output = null, string? Error = null, ClipWaveformDescriptor? Cache = null, ClipThumbnailDescriptor? Thumbnails = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClipMediaRequest))]
[JsonSerializable(typeof(ClipMediaResponse))]
public sealed partial class ClipMediaJsonContext : JsonSerializerContext;
