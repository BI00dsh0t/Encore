using Encore.Contracts.Settings;

namespace Encore.Core.Audio;

/// <summary>The engine's audio devices: one client per endpoint, shared; each stream is QPC-locked 48 kHz stereo float.</summary>
public interface IAudioDeviceHub
{
    /// <summary>Subscribes to an endpoint: loopback for an Output Device, capture for an Input Device; throws on failure.</summary>
    IAudioSubscription Subscribe(string endpointId, AudioFlow flow, IAudioPcmSubscriber subscriber);

    /// <summary>Subscribes to a session's device; a hub may open it by its saved names when its saved id is gone.</summary>
    IAudioSubscription Subscribe(Sessions.SessionAudioDevice device, IAudioPcmSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Subscribe(device.EndpointId, device.Flow, subscriber);
    }
}
