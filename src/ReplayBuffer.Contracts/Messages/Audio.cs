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
public sealed record AudioDevice(string EndpointId, string FriendlyName, string ParentName, AudioFlow Flow, bool Available);

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

    public static IReadOnlyList<CommandType> Commands { get; } = [List];

    public static IReadOnlyList<EventType> Events { get; } = [Changed];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(AudioDeviceList))]
[JsonSerializable(typeof(Empty))]
public sealed partial class AudioJsonContext : JsonSerializerContext;
