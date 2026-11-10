using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>The Audio Tracks editor's draft: Tracks listing the sounds they record. UI thread only; docs/code-notes.md.</summary>
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
        return new(layout.Devices, layout.Tracks.Select(DraftOf));
    }

    /// <summary>The device records <paramref name="track"/> records, in order.</summary>
    public IReadOnlyList<LayoutDevice> SourcesOf(AudioTracksDraftTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return [.. track.DeviceKeys.Select(key => _devices.First(device => device.Key == key))];
    }

    /// <summary>Appends an empty Track named <paramref name="name"/> with a new id.</summary>
    public AudioTracksDraftTrack AddTrack(string name)
    {
        var track = new AudioTracksDraftTrack(Guid.NewGuid(), name, muteAffected: false, []);
        Tracks.Add(track);
        return track;
    }

    /// <summary>Adds a device to a Track, reusing the draft's record of it; returns the record, or null when already there.</summary>
    public LayoutDevice? AddSource(AudioTracksDraftTrack track, AudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(device);
        var record = _devices.FirstOrDefault(d =>
            d.Flow == device.Flow && string.Equals(d.EndpointId, device.EndpointId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            var isMic = device.Flow == AudioFlow.Input;
            record = new LayoutDevice(
                Guid.NewGuid(),
                device.EndpointId,
                device.FriendlyName,
                device.ParentName,
                device.Flow,
                IsMic: isMic);
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
        var index = _devices.FindIndex(device => device.Key == deviceKey);
        if (index >= 0)
        {
            _devices[index] = _devices[index] with { IsMic = isMic };
        }
    }

    /// <summary>Replaces the Tracks with a preset's, built from the devices' defaults, keeping Track 1's id.</summary>
    public void ApplyPreset(TrackLayoutPreset preset, IReadOnlyList<AudioDevice> devices)
    {
        var built = TrackLayoutPresets.Build(preset, ToLayout(), devices);
        foreach (var device in built.Devices)
        {
            var index = _devices.FindIndex(known => known.Key == device.Key);
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
        Tracks.AddRange(built.Tracks.Select(DraftOf));
    }

    private static AudioTracksDraftTrack DraftOf(Track track) => new(track.Id, track.Name, track.MuteAffected, track.DeviceKeys);

    /// <summary>The draft as the <c>trackLayout</c> to save: only the devices some Track records.</summary>
    public TrackLayout ToLayout()
    {
        var used = Tracks.SelectMany(track => track.DeviceKeys).ToHashSet();
        return new TrackLayout(
            [.. _devices.Where(device => used.Contains(device.Key))],
            [.. Tracks.Select(track => new Track(track.Id, track.Name, [.. track.DeviceKeys], track.MuteAffected))]);
    }
}
