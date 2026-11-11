using Encore.Contracts.Messages;

namespace Encore.Contracts.Settings;

/// <summary>The ready-made Track Layouts the Audio Tracks page offers.</summary>
public enum TrackLayoutPreset
{
    /// <summary>One "Everything" Track: the default playback device and the default microphone, mute on.</summary>
    Simple,

    /// <summary>"Everything" as in Simple, then "Game and chat" (default playback) and "Microphone" (default mic, mute on).</summary>
    ForEditing,
}

/// <summary>Builds a preset's Track Layout from the Windows default devices; the engine's first-run seed and the App share it.</summary>
public static class TrackLayoutPresets
{
    public const string EverythingName = "Everything";
    public const string GameAndChatName = "Game and chat";
    public const string MicrophoneName = "Microphone";

    /// <summary>The layout a preset builds from the available default devices, replacing the current one. docs/code-notes.md.</summary>
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

    /// <summary>The engine's first-run seed: the Simple preset, so Track 1 is the default layout's "Everything" Track.</summary>
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
