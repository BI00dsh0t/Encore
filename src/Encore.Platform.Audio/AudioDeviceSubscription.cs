using Encore.Core.Audio;

namespace Encore.Platform.Audio;

/// <summary>One subscriber's tap on a hub device; it also exposes the device's diagnostics.</summary>
public sealed class AudioDeviceSubscription : IAudioSubscription
{
    private readonly AudioDeviceHub _hub;
    private int _disposed;

    internal AudioDeviceSubscription(AudioDeviceHub hub, HubDevice device, IAudioPcmSubscriber subscriber)
    {
        _hub = hub;
        Device = device;
        Subscriber = subscriber;
    }

    /// <summary>The current client's fault; null while the device is dropped and held silent.</summary>
    public Exception? DeviceFault => Device.Fault;

    public Exception? SubscriberFault { get; internal set; }

    /// <summary>The device's stamp health and stream counters; they stay readable after the device closes.</summary>
    public AudioDeviceDiagnostics Diagnostics => Device.Diagnostics;

    internal HubDevice Device { get; }

    internal IAudioPcmSubscriber Subscriber { get; }

    public void Dispose()
    {
        // Before the flag, so a refused disposal can still be made later from another thread.
        if (Device.OnCaptureThread)
        {
            throw new InvalidOperationException("A subscription can't be disposed from its own callback.");
        }

        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _hub.Release(this);
        }
    }
}
