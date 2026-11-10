using Encore.Media;

namespace Encore.Platform.Audio;

/// <summary>A hub device's readouts: raw stamp health and drift, and what its stream did with them.</summary>
public sealed class AudioDeviceDiagnostics(AudioEndpoint endpoint, AudioStampStats stamps, DeviceStream stream)
{
    private AudioEndpoint _endpoint = endpoint;

    /// <summary>The endpoint the device is bound to now; a rebind to a new id changes it.</summary>
    public AudioEndpoint Endpoint
    {
        get => Volatile.Read(ref _endpoint);
        internal set => Volatile.Write(ref _endpoint, value);
    }

    public AudioStampStats Stamps { get; } = stamps;

    public DeviceStream Stream { get; } = stream;
}
