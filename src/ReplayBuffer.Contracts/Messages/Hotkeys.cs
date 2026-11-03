using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>One Hotkey action in <see cref="HotkeyStatus"/>.</summary>
/// <param name="Binding">The configured binding, or <c>null</c> when the action is unbound.</param>
/// <param name="Registered">Whether the engine holds the combination now.</param>
/// <param name="Code">Why it isn't registered, one of <see cref="ErrorCodes"/>; <c>null</c> when registered.</param>
public sealed record HotkeyBindingStatus(HotkeyAction Action, HotkeyBinding? Binding, bool Registered, string? Code);

/// <summary><c>hotkey.status</c> (AD-11): every <see cref="HotkeyAction"/>, in order, and whether it is registered.</summary>
public sealed record HotkeyStatus(IReadOnlyList<HotkeyBindingStatus> Bindings)
{
    /// <summary>Nothing registered: every action <see cref="ErrorCodes.HotkeyUnbound"/>.</summary>
    public static HotkeyStatus Unbound { get; } = new(
        [.. Enum.GetValues<HotkeyAction>().Select(a => new HotkeyBindingStatus(a, null, false, ErrorCodes.HotkeyUnbound))]);

    /// <summary>Equal when the bindings are equal, in order.</summary>
    public bool Equals(HotkeyStatus? other) =>
        other is not null && (ReferenceEquals(this, other) || Bindings.SequenceEqual(other.Bindings));

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var binding in Bindings)
        {
            hash.Add(binding);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// <c>hotkey.pressed</c>: a registered Hotkey was pressed. <see cref="Qpc"/> is the QPC read when the
/// engine received <c>WM_HOTKEY</c> (AD-6).
/// </summary>
public sealed record HotkeyPress(HotkeyAction Action, long Qpc);

/// <summary>Hotkey messages (AD-11): registration status and presses. The SessionOrchestrator publishes presses (AD-19).</summary>
public static class HotkeyMessages
{
    public static EventType<HotkeyStatus> Status { get; } = new(
        "hotkey.status",
        HotkeysJsonContext.Default.HotkeyStatus,
        "hotkeys",
        (snapshot, status) => snapshot with { Hotkeys = status });

    public static EventType<HotkeyPress> Pressed { get; } = new(
        "hotkey.pressed",
        HotkeysJsonContext.Default.HotkeyPress,
        "lastHotkeyPress",
        (snapshot, press) => snapshot with { LastHotkeyPress = press });

    public static IReadOnlyList<CommandType> Commands { get; } = [];

    public static IReadOnlyList<EventType> Events { get; } = [Status, Pressed];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(HotkeyStatus))]
[JsonSerializable(typeof(HotkeyPress))]
public sealed partial class HotkeysJsonContext : JsonSerializerContext;
