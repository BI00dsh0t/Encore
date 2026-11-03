using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Audio;

namespace ReplayBuffer.Core.Feedback;

/// <summary>Whether a Track Layout would record the Feedback Sounds (AD-15 keeps them out of Tracks by warning).</summary>
public static class FeedbackCapture
{
    /// <summary>
    /// The Tracks of <paramref name="layout"/> that mix an Output Device on <paramref name="endpointId"/>,
    /// each with that device, in layout order. An Input Device with the same id doesn't count.
    /// </summary>
    public static IReadOnlyList<(Track Track, LayoutDevice Device)> CapturingTracks(TrackLayout layout, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(endpointId);
        var captured = new Dictionary<Guid, LayoutDevice>();
        foreach (var device in layout.Devices)
        {
            if (AudioEndpointMatch.Same(device.EndpointId, device.Flow, endpointId, AudioFlow.Output))
            {
                captured.TryAdd(device.Key, device);
            }
        }

        var tracks = new List<(Track, LayoutDevice)>();
        foreach (var track in layout.Tracks)
        {
            foreach (var key in track.DeviceKeys)
            {
                if (captured.TryGetValue(key, out var device))
                {
                    tracks.Add((track, device));
                    break;
                }
            }
        }

        return tracks;
    }
}
