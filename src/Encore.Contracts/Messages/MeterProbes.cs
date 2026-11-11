using System.Security.Cryptography;
using System.Text;
using Encore.Contracts.Settings;

namespace Encore.Contracts.Messages;

/// <summary>Per-device meters: the App adds one hidden probe Track per device to the layout it watches. docs/code-notes.md.</summary>
public static class MeterProbes
{
    /// <summary>The probe Tracks' name; the engine meters it like any Track.</summary>
    public const string ProbeName = "probe";

    /// <summary>The id of the probe Track of an endpoint and flow.</summary>
    public static Guid TrackId(string endpointId, AudioFlow flow) => Derive("track", endpointId, flow);

    /// <summary>The key a probe gives a device the layout doesn't hold.</summary>
    public static Guid DeviceKey(string endpointId, AudioFlow flow) => Derive("device", endpointId, flow);

    /// <summary>The layout with one probe Track added per device it holds and per available extra device, each probed once.</summary>
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

    /// <summary>The peak of an endpoint's probe in the levels, or 0 when it has no probe there.</summary>
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
