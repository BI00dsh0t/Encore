using Encore.Contracts;
using Encore.Contracts.Settings;
using Encore.Core.Audio;

namespace Encore.Core.Sessions;

/// <summary>What a session captures: a game's window, or a monitor for a Monitor Recording.</summary>
public enum SessionKind
{
    Game,
    Monitor,
}

/// <summary>The window a session captures, its process and that process's Game Profile (null for Global).</summary>
public sealed record CaptureTarget(int ProcessId, nint Window, string? ProfileId);

/// <summary>One audio device a session captures; IsMic marks a Mic Device, and the names find it after a mixer restart.</summary>
public sealed record SessionAudioDevice(string EndpointId, AudioFlow Flow, string FriendlyName, bool IsMic = false, string ParentName = "");

/// <summary>One Track of a session: its title and the devices it mixes, as indexes into the session's devices.</summary>
public sealed record SessionTrack(string Name, IReadOnlyList<int> DeviceIndexes, bool MuteAffected = false);

/// <summary>A session's audio: the distinct devices it subscribes to, once each, and the Tracks written from them.</summary>
public sealed record SessionAudio(IReadOnlyList<SessionAudioDevice> Devices, IReadOnlyList<SessionTrack> Tracks)
{
    /// <summary>No audio at all.</summary>
    public static SessionAudio None { get; } = new([], []);

    /// <summary>One Track per device, named after the device, in the devices' order.</summary>
    public static SessionAudio OnePerDevice(IReadOnlyList<SessionAudioDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return new SessionAudio(devices, [.. devices.Select((d, i) => new SessionTrack(d.FriendlyName, [i]))]);
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
        bool IsMic(LayoutDevice device) => layout.Devices.Any(d =>
            d.IsMic && AudioEndpointMatch.Same(d.EndpointId, d.Flow, device.EndpointId, device.Flow));

        var devices = new List<SessionAudioDevice>();
        var tracks = new List<SessionTrack>();
        foreach (var track in layout.Tracks)
        {
            var indexes = new List<int>();
            foreach (var key in track.DeviceKeys)
            {
                if (!byKey.TryGetValue(key, out var device))
                {
                    throw new ErrorCodeException(ErrorCodes.InvalidArguments, $"Track '{track.Name}' names device {key}, which the layout doesn't hold.");
                }

                var index = devices.FindIndex(d => AudioEndpointMatch.Same(d.EndpointId, d.Flow, device.EndpointId, device.Flow));
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
        for (var t = 0; t < Tracks.Count; t++)
        {
            if (Tracks[t].DeviceIndexes.Contains(device))
            {
                return t;
            }
        }

        return -1;
    }

    /// <summary>The pairs of a <c>muteAffected</c> Track and a Mic Device that mic mute silences, in Track then device order.</summary>
    public IEnumerable<(SessionTrack Track, SessionAudioDevice Device)> GatedMicInputs() =>
        Tracks
            .Where(t => t.MuteAffected)
            .SelectMany(t => t.DeviceIndexes.Where(i => Devices[i].IsMic).Select(i => (t, Devices[i])));
}

/// <summary>A session's description: kind, target, the settings snapshot resolved once at Start, and audio.</summary>
public sealed record SessionSpec(SessionKind Kind, CaptureTarget Target, EffectiveSettings Snapshot, SessionAudio Audio, bool MicMuted = true)
{
    /// <summary>A Monitor session's screens, the primary first: two or more are composited. Null reads the older key.</summary>
    public IReadOnlyList<Contracts.Messages.DisplayMonitor>? Screens { get; init; }
}
