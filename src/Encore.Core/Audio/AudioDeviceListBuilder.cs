using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Settings;

namespace Encore.Core.Audio;

/// <summary>Builds the device list the App offers (R1): present endpoints, plus the absent devices Track Layouts hold.</summary>
public static class AudioDeviceListBuilder
{
    /// <summary>
    /// Every present device, plus each device of a Track Layout (Global's and every profile's) that
    /// isn't present with its flow, unavailable and named as the layout names it, listed once.
    /// Devices match by endpoint id (ordinal, ignoring case) and flow. Sorted by flow (Output first),
    /// friendly name, parent name (ignoring case), then endpoint id. Pure.
    /// </summary>
    public static AudioDeviceList Build(IReadOnlyList<AudioDevice> present, SettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(present);
        ArgumentNullException.ThrowIfNull(document);

        var seen = new HashSet<(string Id, AudioFlow Flow)>(KeyComparer.Instance);
        var devices = new List<AudioDevice>(present.Count);
        foreach (var device in present)
        {
            if (seen.Add((device.EndpointId, device.Flow)))
            {
                devices.Add(device);
            }
        }

        foreach (var layout in Layouts(document))
        {
            foreach (var device in layout.Devices)
            {
                if (seen.Add((device.EndpointId, device.Flow)))
                {
                    devices.Add(new AudioDevice(device.EndpointId, device.FriendlyName, device.ParentName, device.Flow, Available: false));
                }
            }
        }

        devices.Sort(Compare);
        return new AudioDeviceList(devices);
    }

    private static IEnumerable<TrackLayout> Layouts(SettingsDocument document)
    {
        yield return SettingsResolver.Resolve(document, null).TrackLayout;
        foreach (var id in document.Profiles.Keys)
        {
            yield return SettingsResolver.Resolve(document, id).TrackLayout;
        }
    }

    private static int Compare(AudioDevice a, AudioDevice b)
    {
        var order = a.Flow.CompareTo(b.Flow);
        if (order == 0)
        {
            order = StringComparer.OrdinalIgnoreCase.Compare(a.FriendlyName, b.FriendlyName);
        }

        if (order == 0)
        {
            order = StringComparer.OrdinalIgnoreCase.Compare(a.ParentName, b.ParentName);
        }

        return order != 0 ? order : StringComparer.Ordinal.Compare(a.EndpointId, b.EndpointId);
    }

    private sealed class KeyComparer : IEqualityComparer<(string Id, AudioFlow Flow)>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals((string Id, AudioFlow Flow) x, (string Id, AudioFlow Flow) y) =>
            AudioEndpointMatch.Same(x.Id, x.Flow, y.Id, y.Flow);

        public int GetHashCode((string Id, AudioFlow Flow) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id), obj.Flow);
    }
}
