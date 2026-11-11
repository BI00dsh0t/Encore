using Encore.Contracts;
using Encore.Contracts.Settings;
using Encore.Core.Audio;

namespace Encore.Core.Sessions;

/// <summary>A session's audio: the distinct devices it subscribes to, once each, and the Tracks written from them.</summary>
public sealed record SessionAudio(IReadOnlyList<SessionAudioDevice> Devices, IReadOnlyList<SessionTrack> Tracks)
{
    /// <summary>No audio at all.</summary>
    public static SessionAudio None { get; } = new([], []);

    /// <summary>One Track per device, named after the device, in the devices' order.</summary>
    public static SessionAudio OnePerDevice(IReadOnlyList<SessionAudioDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return new SessionAudio(devices, [.. devices.Select((device, index) => new SessionTrack(device.FriendlyName, [index]))]);
    }

    /// <summary>The session audio of a Track Layout; throws when a Track names a device key the layout lacks.</summary>
    public static SessionAudio FromLayout(TrackLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var byKey = new Dictionary<Guid, LayoutDevice>();
        foreach (var device in layout.Devices)
        {
            byKey.TryAdd(device.Key, device);
        }

        // Any key naming an endpoint and flow as a mic makes that device a mic.
        bool IsMic(LayoutDevice device) => layout.Devices.Any(other =>
            other.IsMic && AudioEndpointMatch.Same(other.EndpointId, other.Flow, device.EndpointId, device.Flow));

        var devices = new List<SessionAudioDevice>();
        var tracks = new List<SessionTrack>();
        foreach (var track in layout.Tracks)
        {
            var indexes = new List<int>();
            foreach (var key in track.DeviceKeys)
            {
                if (!byKey.TryGetValue(key, out var device))
                {
                    throw new ErrorCodeException(
                        ErrorCodes.InvalidArguments,
                        $"Track '{track.Name}' names device {key}, which the layout doesn't hold.");
                }

                var index = devices.FindIndex(held => AudioEndpointMatch.Same(held.EndpointId, held.Flow, device.EndpointId, device.Flow));
                if (index < 0)
                {
                    index = devices.Count;
                    var name = string.IsNullOrWhiteSpace(device.FriendlyName) ? device.EndpointId : device.FriendlyName;
                    devices.Add(new SessionAudioDevice(device.EndpointId, device.Flow, name, IsMic(device), device.ParentName ?? ""));
                }

                if (!indexes.Contains(index))
                {
                    indexes.Add(index);
                }
            }

            tracks.Add(new SessionTrack(track.Name, indexes, track.MuteAffected));
        }

        return new SessionAudio(devices, tracks);
    }

    /// <summary>The first Track that mixes device <paramref name="device"/>, or -1.</summary>
    public int FirstTrackOf(int device)
    {
        for (var track = 0; track < Tracks.Count; track++)
        {
            if (Tracks[track].DeviceIndexes.Contains(device))
            {
                return track;
            }
        }

        return -1;
    }

    /// <summary>The pairs of a <c>muteAffected</c> Track and a Mic Device that mic mute silences, in Track then device order.</summary>
    public IEnumerable<(SessionTrack Track, SessionAudioDevice Device)> GatedMicInputs() =>
        Tracks
            .Where(track => track.MuteAffected)
            .SelectMany(track => track.DeviceIndexes.Where(index => Devices[index].IsMic).Select(index => (track, Devices[index])));
}
