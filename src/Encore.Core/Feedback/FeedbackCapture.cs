using Encore.Contracts.Settings;
using Encore.Core.Audio;

namespace Encore.Core.Feedback;

/// <summary>Whether a Track Layout would record the Feedback Sounds; a warning tells the user to keep them out.</summary>
public static class FeedbackCapture
{
    /// <summary>The Tracks that mix an Output Device on this endpoint, each with that device; an Input Device doesn't count.</summary>
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
