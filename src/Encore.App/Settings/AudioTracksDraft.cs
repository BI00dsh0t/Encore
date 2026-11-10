using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>One Track of an <see cref="AudioTracksDraft"/>: its id (kept across renames and moves), name, mute rule and sources.</summary>
internal sealed class AudioTracksDraftTrack(Guid id, string name, bool muteAffected, IEnumerable<Guid> deviceKeys)
{
    public Guid Id { get; } = id;

    public string Name { get; set; } = name;

    /// <summary>Whether the Mic mute key silences this Track's Mic Devices.</summary>
    public bool MuteAffected { get; set; } = muteAffected;

    /// <summary>The keys of the devices it records, in order.</summary>
    public List<Guid> DeviceKeys { get; } = [.. deviceKeys];
}

/// <summary>
/// The Audio Tracks editor's draft (story 10.4, epic 10's EXPERIENCE.md "The new model", AD-24):
/// Tracks, each listing the sounds it records, over the AD-4 <c>trackLayout</c> shape as it is.
/// <list type="bullet">
/// <item>A device is one record across Tracks: adding a source reuses the record of that endpoint
/// and flow, so "Treat as a microphone" applies on every Track with it.</item>
/// <item>A recording device added through the picker is a Mic Device; a playback device isn't.</item>
/// <item><see cref="ToLayout"/> drops a device on no Track; the devices keep their first-use order.</item>
/// </list>
/// Not thread-safe: the editor's UI thread owns it.
/// </summary>
internal sealed class AudioTracksDraft
{
    // Every record the draft has known, in first-use order; ToLayout keeps those on a Track.
    private readonly List<LayoutDevice> _devices;

    private AudioTracksDraft(IEnumerable<LayoutDevice> devices, IEnumerable<AudioTracksDraftTrack> tracks)
    {
        _devices = [.. devices];
        Tracks = [.. tracks];
    }

    /// <summary>The Tracks in file order.</summary>
    public List<AudioTracksDraftTrack> Tracks { get; }

    public static AudioTracksDraft From(TrackLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new(layout.Devices, layout.Tracks.Select(t => new AudioTracksDraftTrack(t.Id, t.Name, t.MuteAffected, t.DeviceKeys)));
    }

    /// <summary>The device records <paramref name="track"/> records, in order.</summary>
    public IReadOnlyList<LayoutDevice> SourcesOf(AudioTracksDraftTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return [.. track.DeviceKeys.Select(key => _devices.First(d => d.Key == key))];
    }

    /// <summary>Appends an empty Track named <paramref name="name"/> with a new id.</summary>
    public AudioTracksDraftTrack AddTrack(string name)
    {
        var track = new AudioTracksDraftTrack(Guid.NewGuid(), name, muteAffected: false, []);
        Tracks.Add(track);
        return track;
    }

    /// <summary>
    /// Adds <paramref name="device"/> to <paramref name="track"/>, reusing the draft's record of that
    /// endpoint and flow, or adding one with a new key (an input as a Mic Device).
    /// </summary>
    /// <returns>The record, or <c>null</c> when the Track records it already.</returns>
    public LayoutDevice? AddSource(AudioTracksDraftTrack track, AudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(device);
        var record = _devices.FirstOrDefault(d =>
            d.Flow == device.Flow && string.Equals(d.EndpointId, device.EndpointId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            record = new LayoutDevice(Guid.NewGuid(), device.EndpointId, device.FriendlyName, device.ParentName, device.Flow, IsMic: device.Flow == AudioFlow.Input);
            _devices.Add(record);
        }

        if (track.DeviceKeys.Contains(record.Key))
        {
            return null;
        }

        track.DeviceKeys.Add(record.Key);
        return record;
    }

    /// <summary>Removes a source from one Track; a device left on no Track is dropped at <see cref="ToLayout"/>.</summary>
    public void RemoveSource(AudioTracksDraftTrack track, Guid deviceKey)
    {
        ArgumentNullException.ThrowIfNull(track);
        track.DeviceKeys.Remove(deviceKey);
    }

    /// <summary>"Treat as a microphone": sets the record's Mic flag, on every Track with it.</summary>
    public void SetMic(Guid deviceKey, bool isMic)
    {
        var index = _devices.FindIndex(d => d.Key == deviceKey);
        if (index >= 0)
        {
            _devices[index] = _devices[index] with { IsMic = isMic };
        }
    }

    /// <summary>Replaces the Tracks with <paramref name="preset"/>'s from <paramref name="devices"/>' defaults, keeping Track 1's id.</summary>
    public void ApplyPreset(TrackLayoutPreset preset, IReadOnlyList<AudioDevice> devices)
    {
        var built = TrackLayoutPresets.Build(preset, ToLayout(), devices);
        foreach (var device in built.Devices)
        {
            var index = _devices.FindIndex(d => d.Key == device.Key);
            if (index >= 0)
            {
                _devices[index] = device;
            }
            else
            {
                _devices.Add(device);
            }
        }

        Tracks.Clear();
        Tracks.AddRange(built.Tracks.Select(t => new AudioTracksDraftTrack(t.Id, t.Name, t.MuteAffected, t.DeviceKeys)));
    }

    /// <summary>The draft as the <c>trackLayout</c> to save: only the devices some Track records.</summary>
    public TrackLayout ToLayout()
    {
        var used = Tracks.SelectMany(t => t.DeviceKeys).ToHashSet();
        return new TrackLayout(
            [.. _devices.Where(d => used.Contains(d.Key))],
            [.. Tracks.Select(t => new Track(t.Id, t.Name, [.. t.DeviceKeys], t.MuteAffected))]);
    }
}
