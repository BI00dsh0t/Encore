using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Audio;

/// <summary>The engine's one internal audio format: 48 kHz, stereo, 32-bit float, interleaved.</summary>
public static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
}

/// <summary>One block of a device's stream; blocks are contiguous, and a silent block carries no samples.</summary>
public readonly ref struct AudioBlock
{
    public AudioBlock(long startQpc, int frames, ReadOnlySpan<float> interleaved)
    {
        StartQpc = startQpc;
        Frames = frames;
        Interleaved = interleaved;
    }

    /// <summary>The QPC of the block's first frame.</summary>
    public long StartQpc { get; }

    public int Frames { get; }

    /// <summary><see cref="Frames"/> × 2 samples, or empty for a silent block.</summary>
    public ReadOnlySpan<float> Interleaved { get; }

    public bool IsSilent => Interleaved.IsEmpty;
}

/// <summary>Receives a device's blocks on its MMCSS thread, in order; it must not block, and a throw cuts it off.</summary>
public interface IAudioPcmSubscriber
{
    void OnPcm(in AudioBlock block);
}

/// <summary>One subscriber's tap on one device; once Dispose returns, its subscriber gets no further call.</summary>
public interface IAudioSubscription : IDisposable
{
    /// <summary>The device's first failure while capturing, if any; its stream was held silent after it.</summary>
    Exception? DeviceFault { get; }

    /// <summary>What the subscriber threw, if it did; it got no call after that.</summary>
    Exception? SubscriberFault { get; }
}

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

/// <summary>The PC's audio endpoints: the present ones of both flows, and a signal when they change.</summary>
public interface IAudioEndpointSource
{
    /// <summary>Every active endpoint of both flows, with each flow's Windows default marked; throws when they can't be listed.</summary>
    IReadOnlyList<AudioDevice> ListPresent();

    /// <summary>The endpoints or a default changed; raised on any thread, once per burst. Handlers must not block.</summary>
    event Action? EndpointsChanged;
}

/// <summary>An open audio device the hub dropped or resumed: its endpoint, flow and names.</summary>
public sealed record AudioDeviceDropout(string EndpointId, AudioFlow Flow, string FriendlyName, string ParentName);

/// <summary>The hub's dropout signals, raised on its reconcile thread: a device vanished, or it is bound again.</summary>
public interface IAudioDropoutSource
{
    event Action<AudioDeviceDropout>? Dropped;

    event Action<AudioDeviceDropout>? Resumed;
}
