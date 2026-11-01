using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Core.Sessions;

/// <summary>What a session captures: a game's window, or (Monitor Recording, epic 3) a monitor.</summary>
public enum SessionKind
{
    Game,
    Monitor,
}

/// <summary>
/// The window a session captures, the process that owns it, and that process's Game Profile
/// (null for Global). A retarget keeps <see cref="ProcessId"/> (AD-19).
/// </summary>
public sealed record CaptureTarget(int ProcessId, nint Window, string? ProfileId);

/// <summary>One audio device a session captures: loopback for an Output Device, capture for an Input Device (AD-13).</summary>
public sealed record SessionAudioDevice(string EndpointId, AudioFlow Flow, string FriendlyName);

/// <summary>
/// One Track of a session: its stream title, and the devices it mixes, as indexes into
/// <see cref="SessionAudio.Devices"/>.
/// </summary>
public sealed record SessionTrack(string Name, IReadOnlyList<int> DeviceIndexes);

/// <summary>
/// A session's audio: the distinct devices it subscribes to, once each (AD-20), and the Tracks
/// written from them, in stream order; the first Track is the default stream.
/// </summary>
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

    /// <summary>
    /// The session audio of a Track Layout: its Tracks in layout order, and the distinct devices
    /// some Track references, in first-reference order. A device is one endpoint and flow, however
    /// many layout keys name it; a device the layout holds but no Track uses is left out.
    /// </summary>
    /// <exception cref="ErrorCodeException">
    /// <see cref="ErrorCodes.InvalidArguments"/>: a Track has no devices (a silent Track is story 2.2's)
    /// or names a device key the layout doesn't hold.
    /// </exception>
    public static SessionAudio FromLayout(TrackLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var byKey = new Dictionary<Guid, LayoutDevice>();
        foreach (var device in layout.Devices)
        {
            byKey.TryAdd(device.Key, device);
        }

        var devices = new List<SessionAudioDevice>();
        var tracks = new List<SessionTrack>();
        foreach (var track in layout.Tracks)
        {
            if (track.DeviceKeys.Count == 0)
            {
                throw new ErrorCodeException(ErrorCodes.InvalidArguments,
                    $"Track '{track.Name}' has no devices; a Track Layout for a capture needs at least one device in every Track.");
            }

            var indexes = new List<int>();
            foreach (var key in track.DeviceKeys)
            {
                if (!byKey.TryGetValue(key, out var device))
                {
                    throw new ErrorCodeException(ErrorCodes.InvalidArguments, $"Track '{track.Name}' names device {key}, which the layout doesn't hold.");
                }

                var index = devices.FindIndex(d =>
                    d.Flow == device.Flow && string.Equals(d.EndpointId, device.EndpointId, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    index = devices.Count;
                    var name = string.IsNullOrWhiteSpace(device.FriendlyName) ? device.EndpointId : device.FriendlyName;
                    devices.Add(new SessionAudioDevice(device.EndpointId, device.Flow, name));
                }

                if (!indexes.Contains(index))
                {
                    indexes.Add(index);
                }
            }

            tracks.Add(new SessionTrack(track.Name, indexes));
        }

        return new SessionAudio(devices, tracks);
    }
}

/// <summary>
/// A session's description (AD-22): its kind, its target, the settings snapshot resolved once at
/// Start (AD-5), and its audio. Only <see cref="Target"/> changes over a session, on a retarget.
/// </summary>
public sealed record SessionSpec(SessionKind Kind, CaptureTarget Target, EffectiveSettings Snapshot, SessionAudio Audio);
