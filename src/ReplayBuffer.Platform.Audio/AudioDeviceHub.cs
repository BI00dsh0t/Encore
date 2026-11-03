using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Audio;
using ReplayBuffer.Media;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Platform.Audio;

/// <summary>
/// The engine's audio devices (AD-20): at most one <see cref="WasapiCapture"/> per endpoint and
/// flow, opened by the first subscriber and closed by the last. Each device's packets go through
/// one <see cref="DeviceStream"/> (silence fill, stamping, the adaptive resampler, AD-13), whose
/// blocks fan out to the subscribers on the device's thread, under the device's dispatch lock.
/// It is also the engine's <see cref="IAudioEndpointSource"/>: the first <see cref="EndpointsChanged"/>
/// handler starts an <see cref="EndpointWatcher"/>, which runs until the hub is disposed.
/// </summary>
public sealed class AudioDeviceHub : IAudioDeviceHub, IAudioEndpointSource, IDisposable
{
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HubDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private Action? _endpointsChanged;
    private EndpointWatcher? _watcher;

    public AudioDeviceHub(ILoggerFactory loggers)
    {
        _loggers = loggers;
        _log = loggers.CreateLogger<AudioDeviceHub>();
    }

    /// <summary>Devices open now: one per endpoint and flow with a live subscription.</summary>
    public int OpenDeviceCount
    {
        get
        {
            lock (_gate)
            {
                return _devices.Count;
            }
        }
    }

    /// <inheritdoc/>
    public event Action? EndpointsChanged
    {
        add
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _endpointsChanged += value;
                _watcher ??= new EndpointWatcher(RaiseEndpointsChanged, _log);
            }
        }

        remove
        {
            lock (_gate)
            {
                _endpointsChanged -= value;
            }
        }
    }

    /// <summary>The endpoint watcher, once a handler has started it; tests drive its client.</summary>
    internal EndpointWatcher? Watcher
    {
        get
        {
            lock (_gate)
            {
                return _watcher;
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<AudioDevice> ListPresent() =>
    [
        .. AudioEndpoints.List(AudioFlow.Output).Select(e => ToDevice(e, SettingsFlow.Output)),
        .. AudioEndpoints.List(AudioFlow.Input).Select(e => ToDevice(e, SettingsFlow.Input)),
    ];

    /// <inheritdoc/>
    /// <exception cref="Contracts.ErrorCodeException"><see cref="Contracts.ErrorCodes.AudioDeviceFailed"/>: the device can't be opened.</exception>
    public IAudioSubscription Subscribe(string endpointId, SettingsFlow flow, IAudioPcmSubscriber subscriber) => SubscribeDevice(endpointId, flow, subscriber);

    /// <summary><see cref="Subscribe"/>, returning the concrete subscription with its device's diagnostics.</summary>
    public AudioDeviceSubscription SubscribeDevice(string endpointId, SettingsFlow flow, IAudioPcmSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(endpointId);
        ArgumentNullException.ThrowIfNull(subscriber);
        var platformFlow = flow == SettingsFlow.Output ? AudioFlow.Output : AudioFlow.Input;
        var key = (platformFlow == AudioFlow.Output ? "out|" : "in|") + endpointId;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var opened = false;
            if (!_devices.TryGetValue(key, out var device))
            {
                device = new HubDevice(key, new WasapiCapture(endpointId, platformFlow, _loggers.CreateLogger<WasapiCapture>()), _log);
                _devices.Add(key, device);
                opened = true;
            }

            var subscription = new AudioDeviceSubscription(this, device, subscriber);
            device.Add(subscription);
            if (opened)
            {
                device.Start();
            }

            return subscription;
        }
    }

    public void Dispose()
    {
        EndpointWatcher? watcher;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var device in _devices.Values)
            {
                device.Close();
            }

            _devices.Clear();
            watcher = _watcher;
            _watcher = null;
        }

        // Outside the gate: the watcher's thread may be raising, and joining it waits for that.
        watcher?.Dispose();
    }

    private static AudioDevice ToDevice(AudioEndpoint endpoint, SettingsFlow flow) =>
        new(endpoint.Id, endpoint.FriendlyName, endpoint.ParentName, flow, Available: true);

    // On the watcher's thread; reads the handlers without the gate, so disposal can join it.
    private void RaiseEndpointsChanged() => Volatile.Read(ref _endpointsChanged)?.Invoke();

    internal void Release(AudioDeviceSubscription subscription)
    {
        var device = subscription.Device;
        lock (_gate)
        {
            // Waits for a dispatch in flight: no call reaches the subscriber after this.
            device.Remove(subscription);
            if (--device.Subscriptions > 0 || device.Closed)
            {
                return;
            }

            device.Close();
            _devices.Remove(device.Key);
        }
    }
}

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

    public Exception? DeviceFault => Device.Capture.Fault;

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

/// <summary>A hub device's readouts: raw stamp health and drift, and what its stream did with them.</summary>
public sealed class AudioDeviceDiagnostics(AudioEndpoint endpoint, AudioStampStats stamps, DeviceStream stream)
{
    public AudioEndpoint Endpoint { get; } = endpoint;

    public AudioStampStats Stamps { get; } = stamps;

    public DeviceStream Stream { get; } = stream;
}

/// <summary>One open endpoint: its client, its stream and its subscribers.</summary>
internal sealed class HubDevice : IAudioPcmSubscriber
{
    private readonly Lock _dispatch = new();
    private readonly ILogger _log;
    private AudioDeviceSubscription[] _subscribers = [];

    public HubDevice(string key, WasapiCapture capture, ILogger log)
    {
        Key = key;
        Capture = capture;
        _log = log;
        var qpf = Stopwatch.Frequency;
        Diagnostics = new AudioDeviceDiagnostics(capture.Endpoint, new AudioStampStats(qpf), new DeviceStream(qpf, this));
    }

    public string Key { get; }

    public WasapiCapture Capture { get; }

    public AudioDeviceDiagnostics Diagnostics { get; }

    /// <summary>Live subscriptions, cut-off ones included; changed only under the hub's gate.</summary>
    public int Subscriptions { get; set; }

    public bool Closed { get; private set; }

    public bool OnCaptureThread => Capture.OnCaptureThread;

    public void Start() => Capture.Start(Diagnostics.Stream, Diagnostics.Stamps);

    public void Add(AudioDeviceSubscription subscription)
    {
        lock (_dispatch)
        {
            _subscribers = [.. _subscribers, subscription];
        }

        Subscriptions++;
    }

    public void Remove(AudioDeviceSubscription subscription)
    {
        lock (_dispatch)
        {
            RemoveLocked(subscription);
        }
    }

    /// <summary>Stops the thread and releases the client.</summary>
    public void Close()
    {
        Closed = true;
        Capture.Dispose();
        _log.LogInformation("Closed {Flow} device '{Name}'", Capture.Endpoint.Flow, Capture.Endpoint.FriendlyName);
    }

    public void OnPcm(in AudioBlock block)
    {
        lock (_dispatch)
        {
            var subscribers = _subscribers;
            foreach (var subscription in subscribers)
            {
                try
                {
                    subscription.Subscriber.OnPcm(in block);
                }
                catch (Exception e)
                {
                    subscription.SubscriberFault = e;
                    RemoveLocked(subscription);
                    _log.LogError(e, "A subscriber of '{Name}' failed and was cut off", Capture.Endpoint.FriendlyName);
                }
            }
        }
    }

    private void RemoveLocked(AudioDeviceSubscription subscription)
    {
        var index = Array.IndexOf(_subscribers, subscription);
        if (index < 0)
        {
            return;
        }

        _subscribers = [.. _subscribers.AsSpan(0, index), .. _subscribers.AsSpan(index + 1)];
    }
}
