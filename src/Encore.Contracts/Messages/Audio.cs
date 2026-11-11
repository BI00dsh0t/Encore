using System.Text.Json;
using System.Text.Json.Serialization;
using Encore.Contracts.Ipc;
using Encore.Contracts.Settings;

namespace Encore.Contracts.Messages;

/// <summary>An audio device the App can offer: a present endpoint, or one a Track Layout holds that isn't present now.</summary>
public sealed record AudioDevice(
    string EndpointId,
    string FriendlyName,
    string ParentName,
    AudioFlow Flow,
    bool Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsDefault = false);

/// <summary><c>devices.changed</c> and <c>devices.list</c>'s result: present endpoints plus the layouts' absent devices.</summary>
public sealed record AudioDeviceList(IReadOnlyList<AudioDevice> Devices)
{
    /// <summary>No devices: the engine hasn't listed them yet, or it is an older engine.</summary>
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

/// <summary><c>meters.watch</c>'s arguments: the Track Layout to meter, saved or an unsaved draft.</summary>
public sealed record MetersWatch(TrackLayout Layout);

/// <summary>One Track's level: its linear sample peak since the last <c>meters.levels</c>, 0..1, after its limiter.</summary>
public sealed record TrackLevel(Guid TrackId, float Peak);

/// <summary><c>meters.levels</c>: one level per Track of the watched layout, in layout order; none when nothing is watched.</summary>
public sealed record MeterLevels(IReadOnlyList<TrackLevel> Tracks)
{
    /// <summary>No Tracks: nothing is watched, or the engine is an older one.</summary>
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

/// <summary>Audio messages: Track Layouts, devices and meters; this area's payloads and list entries are added here only.</summary>
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

    /// <summary>Meters a Track Layout until <c>meters.unwatch</c> or the next watch; one watch at a time, engine-wide.</summary>
    public static CommandType<MetersWatch, Empty> Watch { get; } =
        new("meters.watch", AudioJsonContext.Default.MetersWatch, AudioJsonContext.Default.Empty);

    /// <summary>Stops the watch, if any; then <c>meters.levels</c> carries no Tracks.</summary>
    public static CommandType<Empty, Empty> Unwatch { get; } =
        new("meters.unwatch", AudioJsonContext.Default.Empty, AudioJsonContext.Default.Empty);

    /// <summary>The watched Tracks' peaks, at most 20 times a second; a connection's queue keeps only the latest.</summary>
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
