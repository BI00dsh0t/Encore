using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.Contracts.Settings;

/// <summary>The Audio Tracks presets (story 10.4, epic 10's EXPERIENCE.md "Presets").</summary>
public enum TrackLayoutPreset
{
    /// <summary>One "Everything" Track: the default playback device and the default microphone, mute on.</summary>
    Simple,

    /// <summary>"Everything" as in <see cref="Simple"/>, then "Game and chat" (default playback) and "Microphone" (default microphone, mute on).</summary>
    ForEditing,
}

/// <summary>
/// Builds a preset's <see cref="TrackLayout"/> from the Windows default devices (AD-24). The engine's
/// first-run seed and the App's presets share it, so the two never differ. Pure, except for the ids
/// of new devices and Tracks.
/// </summary>
public static class TrackLayoutPresets
{
    public const string EverythingName = "Everything";
    public const string GameAndChatName = "Game and chat";
    public const string MicrophoneName = "Microphone";

    /// <summary>
    /// The layout <paramref name="preset"/> builds from <paramref name="devices"/>' available defaults
    /// (<see cref="AudioDevice.IsDefault"/>), replacing <paramref name="current"/>:
    /// <list type="bullet">
    /// <item>Track 1 keeps <paramref name="current"/>'s first Track id (or <see cref="Keys.EverythingTrackId"/>
    /// when it has none); the other Tracks get new ids.</item>
    /// <item>A default device <paramref name="current"/> already holds (same endpoint, ignoring case, and flow)
    /// keeps its record and key; the default input is marked a Mic Device. Others are added with new keys.</item>
    /// <item>A flow with no available default is left out, and its Tracks stay empty.</item>
    /// <item>The layout holds only the devices its Tracks use.</item>
    /// </list>
    /// </summary>
    /// <param name="newId">Makes new device keys and Track ids; <see cref="Guid.NewGuid"/> by default.</param>
    public static TrackLayout Build(TrackLayoutPreset preset, TrackLayout current, IReadOnlyList<AudioDevice> devices, Func<Guid>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(devices);
        newId ??= Guid.NewGuid;

        var output = Resolve(current, DefaultOf(devices, AudioFlow.Output), newId);
        var input = Resolve(current, DefaultOf(devices, AudioFlow.Input), newId);
        var both = KeysOf(output, input);
        var firstId = current.Tracks.Count > 0 ? current.Tracks[0].Id : Keys.EverythingTrackId;

        List<Track> tracks = [new Track(firstId, EverythingName, both, MuteAffected: true)];
        if (preset == TrackLayoutPreset.ForEditing)
        {
            tracks.Add(new Track(newId(), GameAndChatName, KeysOf(output, null), MuteAffected: false));
            tracks.Add(new Track(newId(), MicrophoneName, KeysOf(null, input), MuteAffected: true));
        }
        else if (preset != TrackLayoutPreset.Simple)
        {
            throw new ArgumentOutOfRangeException(nameof(preset), preset, null);
        }

        List<LayoutDevice> used = [];
        if (output is not null)
        {
            used.Add(output);
        }

        if (input is not null)
        {
            used.Add(input);
        }

        return new TrackLayout(used, tracks);
    }

    /// <summary>
    /// The engine's first-run seed (R6): <see cref="TrackLayoutPreset.Simple"/> over the catalog default,
    /// so Track 1 is the default layout's "Everything" Track.
    /// </summary>
    public static TrackLayout Seed(IReadOnlyList<AudioDevice> devices, Func<Guid>? newId = null) =>
        Build(TrackLayoutPreset.Simple, Keys.TrackLayout.Default, devices, newId);

    private static AudioDevice? DefaultOf(IReadOnlyList<AudioDevice> devices, AudioFlow flow) =>
        devices.FirstOrDefault(d => d.Flow == flow && d.IsDefault && d.Available);

    private static LayoutDevice? Resolve(TrackLayout current, AudioDevice? device, Func<Guid> newId)
    {
        if (device is null)
        {
            return null;
        }

        var isMic = device.Flow == AudioFlow.Input;
        var held = current.Devices.FirstOrDefault(d =>
            d.Flow == device.Flow && string.Equals(d.EndpointId, device.EndpointId, StringComparison.OrdinalIgnoreCase));
        return held is not null
            ? held with { IsMic = held.IsMic || isMic }
            : new LayoutDevice(newId(), device.EndpointId, device.FriendlyName, device.ParentName, device.Flow, isMic);
    }

    private static Guid[] KeysOf(LayoutDevice? output, LayoutDevice? input) =>
        [.. new[] { output, input }.OfType<LayoutDevice>().Select(d => d.Key)];
}
