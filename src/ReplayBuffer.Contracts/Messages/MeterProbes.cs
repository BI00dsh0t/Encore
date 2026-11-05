using System.Security.Cryptography;
using System.Text;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>
/// Per-device meters over <c>meters.watch</c> (story 10.4, AD-20, AD-24): the App adds one hidden
/// probe Track per device it wants a level for to the layout it watches, and reads that device's
/// level from its probe's entry in <c>meters.levels</c>. A probe holds its one device with
/// <c>muteAffected</c> false, so it shows the device as it sounds. Probe ids are derived from the
/// endpoint id (ignoring case) and flow, so they're stable across watches. Probes are never saved:
/// they live only in the watched copy. The engine opens each device once whatever the Tracks
/// (AD-20), so a probe adds a mixer input, not a device.
/// </summary>
public static class MeterProbes
{
    /// <summary>The probe Tracks' name; the engine meters it like any Track.</summary>
    public const string ProbeName = "probe";

    /// <summary>The id of the probe Track of an endpoint and flow.</summary>
    public static Guid TrackId(string endpointId, AudioFlow flow) => Derive("track", endpointId, flow);

    /// <summary>The key a probe gives a device the layout doesn't hold.</summary>
    public static Guid DeviceKey(string endpointId, AudioFlow flow) => Derive("device", endpointId, flow);

    /// <summary>
    /// <paramref name="layout"/> with one probe Track appended per device it holds and per available
    /// device of <paramref name="extra"/> (a picker's list), each endpoint and flow probed once. A
    /// device the layout holds keeps its key; another is added with <see cref="DeviceKey"/>, not a Mic Device.
    /// </summary>
    public static TrackLayout WithProbes(TrackLayout layout, IEnumerable<AudioDevice>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var devices = new List<LayoutDevice>(layout.Devices);
        var tracks = new List<Track>(layout.Tracks);
        var probed = new HashSet<Guid>();
        foreach (var device in layout.Devices)
        {
            AddProbe(tracks, probed, device);
        }

        foreach (var device in extra ?? [])
        {
            if (!device.Available || probed.Contains(TrackId(device.EndpointId, device.Flow)))
            {
                continue;
            }

            var added = new LayoutDevice(DeviceKey(device.EndpointId, device.Flow), device.EndpointId, device.FriendlyName, device.ParentName, device.Flow, IsMic: false);
            devices.Add(added);
            AddProbe(tracks, probed, added);
        }

        return new TrackLayout(devices, tracks);
    }

    /// <summary>The level of an endpoint's probe in <paramref name="levels"/>: its peak, or 0 when it has no probe there.</summary>
    public static float LevelOf(MeterLevels levels, string endpointId, AudioFlow flow)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var id = TrackId(endpointId, flow);
        foreach (var level in levels.Tracks)
        {
            if (level.TrackId == id)
            {
                return level.Peak;
            }
        }

        return 0;
    }

    private static void AddProbe(List<Track> tracks, HashSet<Guid> probed, LayoutDevice device)
    {
        var id = TrackId(device.EndpointId, device.Flow);
        if (probed.Add(id))
        {
            tracks.Add(new Track(id, ProbeName, [device.Key], MuteAffected: false));
        }
    }

    // A name-based id: the first 16 bytes of a SHA-256, with RFC 9562's version 8 and variant bits.
    private static Guid Derive(string kind, string endpointId, AudioFlow flow)
    {
        ArgumentNullException.ThrowIfNull(endpointId);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"replaybuffer-meter-probe|{kind}|{flow}|{endpointId.ToUpperInvariant()}"), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
