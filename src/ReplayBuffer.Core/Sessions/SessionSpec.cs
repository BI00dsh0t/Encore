using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Audio;

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

/// <summary>
/// One audio device a session captures: loopback for an Output Device, capture for an Input Device
/// (AD-13). <paramref name="IsMic"/> marks a Mic Device, which mic mute silences in the Tracks it affects.
/// </summary>
public sealed record SessionAudioDevice(string EndpointId, AudioFlow Flow, string FriendlyName, bool IsMic = false);

/// <summary>
/// One Track of a session: its stream title, and the devices it mixes, as indexes into
/// <see cref="SessionAudio.Devices"/>. In a <paramref name="MuteAffected"/> Track, mic mute silences
/// its Mic Devices before the mix (AD-13).
/// </summary>
public sealed record SessionTrack(string Name, IReadOnlyList<int> DeviceIndexes, bool MuteAffected = false);

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
    /// many layout keys name it; a device the layout holds but no Track uses is left out. A Track
    /// with no devices is a silent Track (no device indexes), written as silence. A device is a Mic
    /// Device when any layout key naming it is one (the safe side for a privacy control), and each
    /// Track keeps its <c>muteAffected</c>.
    /// </summary>
    /// <exception cref="ErrorCodeException">
    /// <see cref="ErrorCodes.InvalidArguments"/>: a Track names a device key the layout doesn't hold.
    /// </exception>
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
                    devices.Add(new SessionAudioDevice(device.EndpointId, device.Flow, name, IsMic(device)));
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

    /// <summary>The (<c>muteAffected</c> Track, Mic Device) pairs mic mute silences, in Track order, then each Track's device order.</summary>
    public IEnumerable<(SessionTrack Track, SessionAudioDevice Device)> GatedMicInputs() =>
        Tracks
            .Where(t => t.MuteAffected)
            .SelectMany(t => t.DeviceIndexes.Where(i => Devices[i].IsMic).Select(i => (t, Devices[i])));
}

/// <summary>
/// A session's description (AD-22): its kind, its target, the settings snapshot resolved once at
/// Start (AD-5), and its audio. <see cref="Target"/> changes on a retarget, and a game session's
/// <see cref="Snapshot"/> and <see cref="Audio"/> at a save that switches its encoder chain
/// (<see cref="ICaptureSession.PrepareChain"/>).
/// <paramref name="MicMuted"/> is the mic mute at Start, in place before the first audio frame;
/// later toggles reach the session through <see cref="ICaptureSession.SetMicMuted"/>, and the spec
/// keeps the Start value.
/// </summary>
public sealed record SessionSpec(SessionKind Kind, CaptureTarget Target, EffectiveSettings Snapshot, SessionAudio Audio, bool MicMuted = true);
