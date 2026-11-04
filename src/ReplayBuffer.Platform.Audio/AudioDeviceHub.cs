using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Audio;
using ReplayBuffer.Media;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Platform.Audio;

/// <summary>
/// The engine's audio devices (AD-20): at most one client per endpoint and flow, opened by the
/// first subscriber and closed by the last. Each device's packets go through one
/// <see cref="DeviceStream"/> (silence fill, stamping, the adaptive resampler, AD-13), whose blocks
/// fan out to the subscribers on the device's thread, under the device's dispatch lock.
/// <para>
/// Dropout and rebind (R5): the hub reconciles its open devices against the present endpoints on
/// every endpoint change, when a client reports its device invalidated, and every
/// <see cref="RetryInterval"/> while a device is out. A device that is gone is dropped: its client
/// is closed and a <see cref="SilenceHold"/> keeps its stream continuous. When a matching endpoint
/// is present again (the same id and flow, else the one endpoint of that flow with the same friendly
/// and parent name that no other open device holds), a new client is opened on the same stream, so
/// every subscriber resumes on its old subscription. The rebinding lives only here, never in
/// settings (AD-4).
/// </para>
/// <para>
/// A session's device whose saved id is gone opens by name the same way (story 8.6): a mixer that
/// restarted with new ids, while nothing had the device open, needs nothing re-selected.
/// </para>
/// It is also the engine's <see cref="IAudioEndpointSource"/>, over the PC's endpoints: the first
/// <see cref="EndpointsChanged"/> handler, or the first subscription, starts an <see cref="EndpointWatcher"/>,
/// which runs until the hub is disposed.
/// </summary>
public sealed class AudioDeviceHub : IAudioDeviceHub, IAudioEndpointSource, IAudioDropoutSource, IDisposable
{
    /// <summary>How often a dropped device is retried while it stays out.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>A re-drop this soon after a resume counts as the same dropout.</summary>
    internal static readonly TimeSpan SameDropoutWindow = TimeSpan.FromSeconds(2);

    private readonly ILogger _log;
    private readonly DeviceClientFactory _factory;
    private readonly IAudioEndpointSource _source;
    private readonly bool _ownsSource;
    private readonly Lock _gate = new();
    private readonly Lock _reconcile = new();
    private readonly Dictionary<string, HubDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private bool _watching;
    private Timer? _timer;
    private bool _listingFailed;

    public AudioDeviceHub(ILoggerFactory loggers)
        : this(loggers, WasapiFactory(loggers), new WindowsEndpointSource(loggers.CreateLogger<AudioDeviceHub>()), ownsSource: true)
    {
    }

    /// <summary>A hub over <paramref name="factory"/>'s clients and <paramref name="source"/>'s endpoints; tests pass fakes.</summary>
    internal AudioDeviceHub(ILoggerFactory loggers, DeviceClientFactory factory, IAudioEndpointSource source)
        : this(loggers, factory, source, ownsSource: false)
    {
    }

    private AudioDeviceHub(ILoggerFactory loggers, DeviceClientFactory factory, IAudioEndpointSource source, bool ownsSource)
    {
        _log = loggers.CreateLogger<AudioDeviceHub>();
        _factory = factory;
        _source = source;
        _ownsSource = ownsSource;
    }

    /// <inheritdoc/>
    public event Action<AudioDeviceDropout>? Dropped;

    /// <inheritdoc/>
    public event Action<AudioDeviceDropout>? Resumed;

    /// <inheritdoc/>
    public event Action? EndpointsChanged
    {
        add
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            _source.EndpointsChanged += value;
        }

        remove => _source.EndpointsChanged -= value;
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

    /// <summary>Open devices that are dropped now, held silent.</summary>
    public int DroppedDeviceCount
    {
        get
        {
            lock (_gate)
            {
                return _devices.Values.Count(d => d.Dropped);
            }
        }
    }

    /// <summary>The endpoint watcher, once a handler has started it; tests drive its client.</summary>
    internal EndpointWatcher? Watcher => (_source as WindowsEndpointSource)?.Watcher;

    /// <inheritdoc/>
    public IReadOnlyList<AudioDevice> ListPresent() => _source.ListPresent();

    /// <inheritdoc/>
    /// <exception cref="Contracts.ErrorCodeException"><see cref="Contracts.ErrorCodes.AudioDeviceFailed"/>: the device can't be opened.</exception>
    public IAudioSubscription Subscribe(string endpointId, SettingsFlow flow, IAudioPcmSubscriber subscriber) => SubscribeDevice(endpointId, flow, subscriber);

    /// <inheritdoc/>
    /// <exception cref="Contracts.ErrorCodeException"><see cref="Contracts.ErrorCodes.AudioDeviceFailed"/>: the device can't be opened.</exception>
    public IAudioSubscription Subscribe(Core.Sessions.SessionAudioDevice device, IAudioPcmSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(device);
        return SubscribeDevice(Resolve(device), device.Flow, subscriber);
    }

    /// <summary>
    /// The endpoint a session's device opens: its saved id when that is open or present, else the one
    /// present endpoint of its flow with its friendly and parent name, else its saved id (which then
    /// fails to open as before). Settings are never touched (AD-4).
    /// </summary>
    internal string Resolve(Core.Sessions.SessionAudioDevice device)
    {
        var id = device.EndpointId;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_devices.ContainsKey(KeyOf(id, ToPlatform(device.Flow))))
            {
                return id;
            }
        }

        IReadOnlyList<AudioDevice> present;
        try
        {
            present = _source.ListPresent();
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Couldn't list the audio endpoints; opening '{Name}' by its saved id", device.FriendlyName);
            return id;
        }

        if (present.Any(p => p.Flow == device.Flow && string.Equals(p.EndpointId, id, StringComparison.OrdinalIgnoreCase)))
        {
            return id;
        }

        var byName = present
            .Where(p => p.Flow == device.Flow
                && string.Equals(p.FriendlyName, device.FriendlyName, StringComparison.Ordinal)
                && string.Equals(p.ParentName, device.ParentName, StringComparison.Ordinal))
            .ToList();
        if (byName.Count != 1)
        {
            return id;
        }

        _log.LogInformation(
            "'{Name}' ({Parent}) isn't present as {FormerId}; opening {Id}, the endpoint with its name",
            device.FriendlyName,
            device.ParentName,
            id,
            byName[0].EndpointId);
        return byName[0].EndpointId;
    }

    /// <summary><see cref="Subscribe(string, SettingsFlow, IAudioPcmSubscriber)"/>, returning the concrete subscription with its device's diagnostics.</summary>
    public AudioDeviceSubscription SubscribeDevice(string endpointId, SettingsFlow flow, IAudioPcmSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(endpointId);
        ArgumentNullException.ThrowIfNull(subscriber);
        var platformFlow = ToPlatform(flow);
        var key = KeyOf(endpointId, platformFlow);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StartWatching();
            var opened = false;
            if (!_devices.TryGetValue(key, out var device))
            {
                HubDevice? created = null;
                var client = _factory(endpointId, platformFlow, () => OnInvalidated(created));
                created = new HubDevice(key, client, _log);
                device = created;
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
        Timer? timer;
        bool watching;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timer = _timer;
            _timer = null;
            watching = _watching;
        }

        // Outside the gate: a reconcile in flight takes it, and this waits for that to finish.
        if (timer is not null)
        {
            using var done = new ManualResetEvent(false);
            if (timer.Dispose(done))
            {
                done.WaitOne();
            }
        }

        if (watching)
        {
            _source.EndpointsChanged -= OnEndpointsChanged;
        }

        lock (_gate)
        {
            foreach (var device in _devices.Values)
            {
                device.Close();
            }

            _devices.Clear();
        }

        if (_ownsSource && _source is IDisposable source)
        {
            source.Dispose();
        }
    }

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

            // A dropped device's hold stops here, and with it out of the map it is retried no more.
            device.Close();
            _devices.Remove(device.Key);
        }
    }

    private static DeviceClientFactory WasapiFactory(ILoggerFactory loggers) =>
        (endpointId, flow, invalidated) => new WasapiCapture(endpointId, flow, loggers.CreateLogger<WasapiCapture>(), invalidated);

    private static AudioFlow ToPlatform(SettingsFlow flow) => flow == SettingsFlow.Output ? AudioFlow.Output : AudioFlow.Input;

    private static SettingsFlow ToSettings(AudioFlow flow) => flow == AudioFlow.Output ? SettingsFlow.Output : SettingsFlow.Input;

    private static string KeyOf(string endpointId, AudioFlow flow) => (flow == AudioFlow.Output ? "out|" : "in|") + endpointId;

    private static AudioDeviceDropout ToDropout(AudioEndpoint endpoint) =>
        new(endpoint.Id, ToSettings(endpoint.Flow), endpoint.FriendlyName, endpoint.ParentName);

    private static bool IsListed(IReadOnlyList<AudioDevice> present, AudioEndpoint endpoint)
    {
        var flow = ToSettings(endpoint.Flow);
        return present.Any(p => p.Flow == flow && string.Equals(p.EndpointId, endpoint.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>On the first subscription: reconciles on every endpoint change from then on. Under the gate.</summary>
    private void StartWatching()
    {
        if (_watching)
        {
            return;
        }

        _timer = new Timer(_ => Reconcile(), null, Timeout.Infinite, Timeout.Infinite);
        _source.EndpointsChanged += OnEndpointsChanged;
        _watching = true;
    }

    private void OnEndpointsChanged() => Schedule(TimeSpan.Zero);

    /// <summary>On a client's thread: only flags the device and wakes the reconcile; never takes the gate.</summary>
    private void OnInvalidated(HubDevice? device)
    {
        if (device is null)
        {
            return;
        }

        device.Invalidated = true;
        Schedule(TimeSpan.Zero);
    }

    private void Schedule(TimeSpan due)
    {
        try
        {
            Volatile.Read(ref _timer)?.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// On the timer: drops every open device whose endpoint is gone or invalidated, rebinds every
    /// dropped one that has a match, then logs and raises the events outside the gate. Serialized;
    /// runs again in <see cref="RetryInterval"/> while a device stays out.
    /// </summary>
    private void Reconcile()
    {
        lock (_reconcile)
        {
            IReadOnlyList<AudioDevice> present;
            try
            {
                present = _source.ListPresent();
                _listingFailed = false;
            }
            catch (Exception e)
            {
                // Nothing is dropped or rebound on a failed listing; one Warning per run of failures.
                if (!_listingFailed)
                {
                    _log.LogWarning(e, "Couldn't list the audio endpoints; open devices are checked again in {Retry}", RetryInterval);
                }
                else
                {
                    _log.LogDebug(e, "Couldn't list the audio endpoints");
                }

                _listingFailed = true;
                lock (_gate)
                {
                    if (!_disposed && _devices.Count > 0)
                    {
                        Schedule(RetryInterval);
                    }
                }

                return;
            }

            var dropped = new List<AudioDeviceDropout>();
            var resumed = new List<(AudioDeviceDropout Endpoint, string? FormerId)>();
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var now = Stopwatch.GetTimestamp();
                var redropped = new List<HubDevice>();
                foreach (var device in _devices.Values)
                {
                    if (!device.Closed && !device.Dropped && (device.Invalidated || !IsListed(present, device.Endpoint)))
                    {
                        // A re-drop soon after a resume (a device still restarting) is the same
                        // dropout: no event, no Warning, and no rebind until the next retry.
                        var sameEpisode = device.ResumedAt != 0 && now - device.ResumedAt < Stopwatch.Frequency * (long)SameDropoutWindow.TotalSeconds;
                        device.Drop();
                        device.QuietEpisode = sameEpisode;
                        device.DroppedAt = now;
                        if (sameEpisode)
                        {
                            redropped.Add(device);
                            _log.LogDebug("'{Name}' dropped again just after resuming; the same dropout", device.Endpoint.FriendlyName);
                        }
                        else
                        {
                            dropped.Add(ToDropout(device.Endpoint));
                        }
                    }
                }

                var retry = false;
                foreach (var device in _devices.Values.Where(d => d.Dropped && !d.Closed).ToList())
                {
                    var formerId = device.Endpoint.Id;
                    if (redropped.Contains(device))
                    {
                        retry = true;
                    }
                    else if (TryRebind(device, present))
                    {
                        device.ResumedAt = Stopwatch.GetTimestamp();
                        if (device.QuietEpisode)
                        {
                            device.QuietEpisode = false;
                            _log.LogDebug("'{Name}' resumed again", device.Endpoint.FriendlyName);
                            continue;
                        }

                        var changed = !string.Equals(formerId, device.Endpoint.Id, StringComparison.OrdinalIgnoreCase);
                        resumed.Add((ToDropout(device.Endpoint), changed ? formerId : null));
                    }
                    else
                    {
                        // A quiet re-drop that outlasts the window is a new outage: report it now.
                        if (device.QuietEpisode && now - device.DroppedAt >= Stopwatch.Frequency * (long)SameDropoutWindow.TotalSeconds)
                        {
                            device.QuietEpisode = false;
                            dropped.Add(ToDropout(device.Endpoint));
                        }

                        retry = true;
                    }
                }

                if (retry)
                {
                    Schedule(RetryInterval);
                }
            }

            foreach (var dropout in dropped)
            {
                _log.LogWarning(
                    "{Code}: '{Name}' ({Parent}) dropped out; held silent until it returns",
                    Contracts.ErrorCodes.DeviceDropout,
                    dropout.FriendlyName,
                    dropout.ParentName);
                Raise(Dropped, dropout);
            }

            foreach (var (endpoint, formerId) in resumed)
            {
                if (formerId is null)
                {
                    _log.LogInformation("'{Name}' ({Parent}) returned; resumed", endpoint.FriendlyName, endpoint.ParentName);
                }
                else
                {
                    _log.LogInformation(
                        "'{Name}' ({Parent}) returned as {Id} (was {FormerId}); resumed",
                        endpoint.FriendlyName,
                        endpoint.ParentName,
                        endpoint.EndpointId,
                        formerId);
                }

                Raise(Resumed, endpoint);
            }
        }
    }

    /// <summary>Opens a client on a dropped device's match and hands it the stream; false when the device stays dropped. Under the gate.</summary>
    private bool TryRebind(HubDevice device, IReadOnlyList<AudioDevice> present)
    {
        var match = FindMatch(device, present);
        if (match is null)
        {
            return false;
        }

        IDeviceClient client;
        try
        {
            client = _factory(match.EndpointId, device.Endpoint.Flow, () => OnInvalidated(device));
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "'{Name}' is listed again but couldn't be opened; retrying", device.Endpoint.FriendlyName);
            return false;
        }

        try
        {
            device.Rebind(client);
        }
        catch (Exception e)
        {
            client.Dispose();
            device.Hold();
            _log.LogDebug(e, "'{Name}' couldn't be restarted; retrying", device.Endpoint.FriendlyName);
            return false;
        }

        // A new id re-keys the device, so the next subscriber to that endpoint shares this client (AD-20).
        var key = KeyOf(device.Endpoint.Id, device.Endpoint.Flow);
        if (!string.Equals(key, device.Key, StringComparison.OrdinalIgnoreCase))
        {
            _devices.Remove(device.Key);
            device.Key = key;
            _devices.Add(key, device);
        }

        return true;
    }

    /// <summary>
    /// The present endpoint a dropped device rebinds to: the same id and flow; else the one endpoint
    /// of that flow with the same friendly and parent name, unless another open device holds it.
    /// </summary>
    private AudioDevice? FindMatch(HubDevice device, IReadOnlyList<AudioDevice> present)
    {
        var endpoint = device.Endpoint;
        var flow = ToSettings(endpoint.Flow);
        var byId = present.FirstOrDefault(p => p.Flow == flow && string.Equals(p.EndpointId, endpoint.Id, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId;
        }

        // Two dropped devices that share a name can't tell which return is whose: only ids rebind them.
        if (_devices.Values.Any(other => !ReferenceEquals(other, device) && !other.Closed && other.Dropped && SameName(other.Endpoint, endpoint)))
        {
            _log.LogDebug(
                "'{Name}' ({Parent}) shares its name with another dropped device; it rebinds only by id",
                endpoint.FriendlyName,
                endpoint.ParentName);
            return null;
        }

        // Endpoints another open device holds are never candidates, so a present twin doesn't make a return ambiguous.
        var byName = present
            .Where(p => p.Flow == flow
                && string.Equals(p.FriendlyName, endpoint.FriendlyName, StringComparison.Ordinal)
                && string.Equals(p.ParentName, endpoint.ParentName, StringComparison.Ordinal)
                && !_devices.Values.Any(other => !ReferenceEquals(other, device)
                    && !other.Closed
                    && other.Endpoint.Flow == endpoint.Flow
                    && string.Equals(other.Endpoint.Id, p.EndpointId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (byName.Count > 1)
        {
            _log.LogDebug(
                "'{Name}' ({Parent}) matches {Count} free present endpoints by name and none by id; it stays dropped",
                endpoint.FriendlyName,
                endpoint.ParentName,
                byName.Count);
            return null;
        }

        return byName.Count == 1 ? byName[0] : null;
    }

    private static bool SameName(AudioEndpoint a, AudioEndpoint b) =>
        a.Flow == b.Flow
        && string.Equals(a.FriendlyName, b.FriendlyName, StringComparison.Ordinal)
        && string.Equals(a.ParentName, b.ParentName, StringComparison.Ordinal);

    private void Raise(Action<AudioDeviceDropout>? handlers, AudioDeviceDropout dropout)
    {
        try
        {
            handlers?.Invoke(dropout);
        }
        catch (Exception e)
        {
            _log.LogError(e, "An audio dropout handler failed");
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

/// <summary>
/// One open endpoint: its client (or, while it is dropped, a silence hold), its stream and its
/// subscribers. The client, the hold and the flags change only under the hub's gate.
/// </summary>
internal sealed class HubDevice : IAudioPcmSubscriber
{
    private readonly Lock _dispatch = new();
    private readonly ILogger _log;
    private AudioDeviceSubscription[] _subscribers = [];
    private volatile IDeviceClient? _client;
    private volatile SilenceHold? _hold;
    private volatile bool _invalidated;
    private volatile Exception? _closedFault;

    public HubDevice(string key, IDeviceClient client, ILogger log)
    {
        Key = key;
        _client = client;
        Endpoint = client.Endpoint;
        _log = log;
        var qpf = Stopwatch.Frequency;
        Diagnostics = new AudioDeviceDiagnostics(client.Endpoint, new AudioStampStats(qpf), new DeviceStream(qpf, this));
    }

    /// <summary>The hub's key: flow and current endpoint id.</summary>
    public string Key { get; set; }

    /// <summary>The client writing the stream now; null while dropped.</summary>
    public IDeviceClient? Client => _client;

    /// <summary>The endpoint the device is bound to now.</summary>
    public AudioEndpoint Endpoint { get; private set; }

    public AudioDeviceDiagnostics Diagnostics { get; }

    /// <summary>Live subscriptions, cut-off ones included; changed only under the hub's gate.</summary>
    public int Subscriptions { get; set; }

    public bool Closed { get; private set; }

    /// <summary>Whether the device is out: its client closed and its stream held silent.</summary>
    public bool Dropped { get; private set; }

    /// <summary>Set from the client's thread when its device was invalidated; the next reconcile drops it.</summary>
    public bool Invalidated
    {
        get => _invalidated;
        set => _invalidated = value;
    }

    /// <summary>The current client's fault, or the last client's once the device closed; null while dropped.</summary>
    public Exception? Fault => _client?.Fault ?? _closedFault;

    /// <summary>When the device last resumed from a dropout (QPC), or 0; under the hub's gate.</summary>
    public long ResumedAt { get; set; }

    /// <summary>When the device was last dropped (QPC); under the hub's gate.</summary>
    public long DroppedAt { get; set; }

    /// <summary>
    /// Whether the current dropout continues one already reported, so its resume is quiet; it is
    /// reported after all once it outlasts the same-dropout window. Under the hub's gate.
    /// </summary>
    public bool QuietEpisode { get; set; }

    public bool OnCaptureThread => _client?.OnCaptureThread == true || _hold?.OnCaptureThread == true;

    public void Start() => _client!.Start(Diagnostics.Stream, Diagnostics.Stamps);

    /// <summary>Closes the client (joining its thread) and holds the stream silent in its place.</summary>
    public void Drop()
    {
        var client = _client;
        _client = null;
        client?.Dispose();
        Dropped = true;
        _invalidated = false;
        Hold();
    }

    /// <summary>Starts the silence hold, if none runs.</summary>
    public void Hold() => _hold ??= new SilenceHold(Diagnostics.Stream, Endpoint.FriendlyName, _log);

    /// <summary>Stops the hold (joining its thread) and starts <paramref name="client"/> on the same stream.</summary>
    public void Rebind(IDeviceClient client)
    {
        var hold = _hold;
        _hold = null;
        hold?.Dispose();

        // Cleared before the start, so an invalidation of the new client is kept.
        _invalidated = false;

        // Set before the start, so a subscriber's callback on the new thread is seen as on it.
        _client = client;
        try
        {
            client.Start(Diagnostics.Stream, Diagnostics.Stamps);
        }
        catch
        {
            _client = null;
            throw;
        }

        Dropped = false;
        Endpoint = client.Endpoint;
        Diagnostics.Endpoint = client.Endpoint;
    }

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
        var client = _client;
        _closedFault = client?.Fault;
        _client = null;
        client?.Dispose();
        _closedFault ??= client?.Fault;
        var hold = _hold;
        _hold = null;
        hold?.Dispose();
        _log.LogInformation("Closed {Flow} device '{Name}'", Endpoint.Flow, Endpoint.FriendlyName);
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
                    _log.LogError(e, "A subscriber of '{Name}' failed and was cut off", Endpoint.FriendlyName);
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
