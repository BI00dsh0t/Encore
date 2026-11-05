using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>
/// An audio device the App can offer (R1): a present endpoint, or one a Track Layout holds that
/// isn't present now (<see cref="Available"/> false). Two devices with the same
/// <see cref="FriendlyName"/> are told apart by <see cref="ParentName"/>.
/// </summary>
/// <param name="EndpointId">The MMDevice endpoint id.</param>
/// <param name="ParentName">The name of the audio device that exposes the endpoint, or empty.</param>
/// <param name="Available">Whether the endpoint is present and active now.</param>
/// <param name="IsDefault">
/// Whether it is the Windows default endpoint of its flow (console role), story 10.4. Optional
/// (AD-22): written only when true, and false from an engine that predates it.
/// </param>
public sealed record AudioDevice(
    string EndpointId,
    string FriendlyName,
    string ParentName,
    AudioFlow Flow,
    bool Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsDefault = false);

/// <summary>
/// <c>devices.changed</c> and <c>devices.list</c>'s result: the present endpoints of both flows, with
/// the absent devices of every Track Layout, Output Devices first, then by name.
/// </summary>
public sealed record AudioDeviceList(IReadOnlyList<AudioDevice> Devices)
{
    /// <summary>No devices: an engine that hasn't listed them, or from before story 2.3.</summary>
    public static AudioDeviceList Empty { get; } = new([]);

    /// <summary>Equal when the devices are, in order.</summary>
    public bool Equals(AudioDeviceList? other) =>
        other is not null && (ReferenceEquals(this, other) || Devices.SequenceEqual(other.Devices));

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var device in Devices)
        {
            hash.Add(device);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// <c>meters.watch</c>'s arguments (R6): the Track Layout to meter, saved or an unsaved draft.
/// </summary>
public sealed record MetersWatch(TrackLayout Layout);

/// <summary>One Track's level: its linear sample peak since the last <c>meters.levels</c>, 0..1, after its limiter.</summary>
public sealed record TrackLevel(Guid TrackId, float Peak);

/// <summary>
/// <c>meters.levels</c> (R6): one level per Track of the watched layout, in layout order; no Tracks
/// when nothing is watched.
/// </summary>
public sealed record MeterLevels(IReadOnlyList<TrackLevel> Tracks)
{
    /// <summary>No Tracks: nothing watched, or an engine from before story 2.6.</summary>
    public static MeterLevels Empty { get; } = new([]);

    /// <summary>Equal when the levels are, in order.</summary>
    public bool Equals(MeterLevels? other) =>
        other is not null && (ReferenceEquals(this, other) || Tracks.SequenceEqual(other.Tracks));

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var track in Tracks)
        {
            hash.Add(track);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Audio messages (epic-multi-track-audio): Track Layouts, devices and meters. Coalescing events such as <c>meters.levels</c> go here.
/// The area's payloads, its own JSON context and its entries in these lists are added here only (AD-22).
/// </summary>
public static class AudioMessages
{
    /// <summary>The current device list, as the snapshot's <c>devices</c> holds it.</summary>
    public static CommandType<Empty, AudioDeviceList> List { get; } =
        new("devices.list", AudioJsonContext.Default.Empty, AudioJsonContext.Default.AudioDeviceList);

    /// <summary>A device was added, removed, enabled, disabled or renamed, or a Track Layout's devices changed.</summary>
    public static EventType<AudioDeviceList> Changed { get; } = new(
        "devices.changed",
        AudioJsonContext.Default.AudioDeviceList,
        "devices",
        (snapshot, devices) => snapshot with { Devices = devices });

    /// <summary>
    /// Meters a Track Layout (R6) until <c>meters.unwatch</c> or the next watch, which replaces it;
    /// one watch at a time, engine-wide. Replies <c>invalid-arguments</c> for a Track naming a device
    /// key the layout lacks, keeping the previous watch.
    /// </summary>
    public static CommandType<MetersWatch, Empty> Watch { get; } =
        new("meters.watch", AudioJsonContext.Default.MetersWatch, AudioJsonContext.Default.Empty);

    /// <summary>Stops the watch, if any; then <c>meters.levels</c> carries no Tracks.</summary>
    public static CommandType<Empty, Empty> Unwatch { get; } =
        new("meters.unwatch", AudioJsonContext.Default.Empty, AudioJsonContext.Default.Empty);

    /// <summary>The watched Tracks' peaks, at most 20 times a second. Coalescing: a connection's queue keeps only the latest.</summary>
    public static EventType<MeterLevels> Levels { get; } = new(
        "meters.levels",
        AudioJsonContext.Default.MeterLevels,
        "meters",
        (snapshot, levels) => snapshot with { Meters = levels },
        coalesces: true);

    public static IReadOnlyList<CommandType> Commands { get; } = [List, Watch, Unwatch];

    public static IReadOnlyList<EventType> Events { get; } = [Changed, Levels];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(AudioDeviceList))]
[JsonSerializable(typeof(Empty))]
[JsonSerializable(typeof(MetersWatch))]
[JsonSerializable(typeof(MeterLevels))]
public sealed partial class AudioJsonContext : JsonSerializerContext;
