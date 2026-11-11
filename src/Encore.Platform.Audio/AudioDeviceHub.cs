using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Contracts.Messages;
using Encore.Core.Audio;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Platform.Audio;

/// <summary>The engine's audio devices: one client per endpoint, shared by its subscribers. Rules: docs/code-notes.md.</summary>
public sealed class AudioDeviceHub : IAudioDeviceHub, IAudioEndpointSource, IAudioDropoutSource, IDisposable
{
    /// <summary>How often a dropped device is retried while it stays out.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>A re-drop this soon after a resume counts as the same dropout.</summary>
    internal static readonly TimeSpan SameDropoutWindow = TimeSpan.FromSeconds(2);

    private static readonly long SameDropoutTicks = Stopwatch.Frequency * (long)SameDropoutWindow.TotalSeconds;

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
                return _devices.Values.Count(device => device.Dropped);
            }
        }
    }

    /// <summary>The endpoint watcher, once a handler has started it; tests drive its client.</summary>
    internal EndpointWatcher? Watcher => (_source as WindowsEndpointSource)?.Watcher;

    /// <inheritdoc/>
    public IReadOnlyList<AudioDevice> ListPresent() => _source.ListPresent();

    /// <inheritdoc/>
    public IAudioSubscription Subscribe(string endpointId, SettingsFlow flow, IAudioPcmSubscriber subscriber) =>
        SubscribeDevice(endpointId, flow, subscriber);

    /// <inheritdoc/>
    public IAudioSubscription Subscribe(Core.Sessions.SessionAudioDevice device, IAudioPcmSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(device);
        return SubscribeDevice(Resolve(device), device.Flow, subscriber);
    }

    /// <summary>The endpoint a session's device opens: its saved id when present, else the one endpoint with the same names.</summary>
    internal string Resolve(Core.Sessions.SessionAudioDevice device)
    {
        var id = device.EndpointId;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_devices.ContainsKey(KeyOf(id, EndpointMatching.ToPlatform(device.Flow))))
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

        if (EndpointMatching.ById(present, device.Flow, id) is not null)
        {
            return id;
        }

        var byName = present.Where(endpoint => EndpointMatching.HasNames(endpoint, device.Flow, device.FriendlyName, device.ParentName))
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

    /// <summary>Subscribes and returns the concrete subscription, which carries its device's diagnostics.</summary>
    public AudioDeviceSubscription SubscribeDevice(string endpointId, SettingsFlow flow, IAudioPcmSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(endpointId);
        ArgumentNullException.ThrowIfNull(subscriber);
        var platformFlow = EndpointMatching.ToPlatform(flow);
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
            WaitForTimer(timer);
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

    private static string KeyOf(string endpointId, AudioFlow flow) => (flow == AudioFlow.Output ? "out|" : "in|") + endpointId;

    private static AudioDeviceDropout ToDropout(AudioEndpoint endpoint) =>
        new(endpoint.Id, EndpointMatching.ToSettings(endpoint.Flow), endpoint.FriendlyName, endpoint.ParentName);

    private static void WaitForTimer(Timer timer)
    {
        using var done = new ManualResetEvent(false);
        if (timer.Dispose(done))
        {
            done.WaitOne();
        }
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

    /// <summary>On the timer: drops devices whose endpoint is gone, rebinds dropped ones that have a match, then raises events.</summary>
    private void Reconcile()
    {
        lock (_reconcile)
        {
            if (TryListPresent() is not { } present)
            {
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
                var redropped = DropMissing(present, now, dropped);
                if (RebindDropped(present, now, redropped, dropped, resumed))
                {
                    Schedule(RetryInterval);
                }
            }

            Announce(dropped, resumed);
        }
    }

    /// <summary>The present endpoints; null when the listing failed, which drops and rebinds nothing and retries later.</summary>
    private IReadOnlyList<AudioDevice>? TryListPresent()
    {
        try
        {
            var present = _source.ListPresent();
            _listingFailed = false;
            return present;
        }
        catch (Exception e)
        {
            // One Warning per run of failures.
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

            return null;
        }
    }

    /// <summary>Drops each device whose endpoint is gone or invalidated; returns those back in the same dropout. Under the gate.</summary>
    private List<HubDevice> DropMissing(IReadOnlyList<AudioDevice> present, long now, List<AudioDeviceDropout> dropped)
    {
        var redropped = new List<HubDevice>();
        foreach (var device in _devices.Values)
        {
            if (device.Closed || device.Dropped || (!device.Invalidated && EndpointMatching.IsListed(present, device.Endpoint)))
            {
                continue;
            }

            // A re-drop soon after a resume is the same dropout: no event, no warning, and no rebind until the next retry.
            var sameEpisode = device.ResumedAt != 0 && now - device.ResumedAt < SameDropoutTicks;
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

        return redropped;
    }

    /// <summary>Rebinds each dropped device that has a match; true when one stays dropped and is retried. Under the gate.</summary>
    private bool RebindDropped(
        IReadOnlyList<AudioDevice> present,
        long now,
        List<HubDevice> redropped,
        List<AudioDeviceDropout> dropped,
        List<(AudioDeviceDropout Endpoint, string? FormerId)> resumed)
    {
        var retry = false;
        foreach (var device in _devices.Values.Where(device => device.Dropped && !device.Closed).ToList())
        {
            if (redropped.Contains(device))
            {
                retry = true;
                continue;
            }

            if (TryResume(device, present, resumed))
            {
                continue;
            }

            // A quiet re-drop that outlasts the window is a new outage: report it now.
            if (device.QuietEpisode && now - device.DroppedAt >= SameDropoutTicks)
            {
                device.QuietEpisode = false;
                dropped.Add(ToDropout(device.Endpoint));
            }

            retry = true;
        }

        return retry;
    }

    /// <summary>Rebinds a dropped device; true when it resumed, its resume listed unless it ended a quiet re-drop.</summary>
    private bool TryResume(
        HubDevice device, IReadOnlyList<AudioDevice> present, List<(AudioDeviceDropout Endpoint, string? FormerId)> resumed)
    {
        var formerId = device.Endpoint.Id;
        if (!TryRebind(device, present))
        {
            return false;
        }

        device.ResumedAt = Stopwatch.GetTimestamp();
        if (device.QuietEpisode)
        {
            device.QuietEpisode = false;
            _log.LogDebug("'{Name}' resumed again", device.Endpoint.FriendlyName);
            return true;
        }

        var changed = !string.Equals(formerId, device.Endpoint.Id, StringComparison.OrdinalIgnoreCase);
        resumed.Add((ToDropout(device.Endpoint), changed ? formerId : null));
        return true;
    }

    /// <summary>Logs and raises the dropouts, then the resumes; outside the gate, so handlers may call back.</summary>
    private void Announce(List<AudioDeviceDropout> dropped, List<(AudioDeviceDropout Endpoint, string? FormerId)> resumed)
    {
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

    /// <summary>Opens a client on a dropped device's match; false when the device stays dropped. Under the gate.</summary>
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

        // A new id re-keys the device, so the next subscriber to that endpoint shares this client.
        var key = KeyOf(device.Endpoint.Id, device.Endpoint.Flow);
        if (!string.Equals(key, device.Key, StringComparison.OrdinalIgnoreCase))
        {
            _devices.Remove(device.Key);
            device.Key = key;
            _devices.Add(key, device);
        }

        return true;
    }

    /// <summary>The endpoint a dropped device rebinds to: the same id, else the only one of its flow with the same names.</summary>
    private AudioDevice? FindMatch(HubDevice device, IReadOnlyList<AudioDevice> present)
    {
        var endpoint = device.Endpoint;
        var flow = EndpointMatching.ToSettings(endpoint.Flow);
        if (EndpointMatching.ById(present, flow, endpoint.Id) is { } byId)
        {
            return byId;
        }

        // Two dropped devices that share a name can't tell which return is whose: only ids rebind them.
        if (_devices.Values.Any(other => !ReferenceEquals(other, device)
            && !other.Closed
            && other.Dropped
            && EndpointMatching.SameName(other.Endpoint, endpoint)))
        {
            _log.LogDebug(
                "'{Name}' ({Parent}) shares its name with another dropped device; it rebinds only by id",
                endpoint.FriendlyName,
                endpoint.ParentName);
            return null;
        }

        // Endpoints another open device holds are never candidates, so a present twin doesn't make a return unclear.
        var byName = present
            .Where(candidate => EndpointMatching.HasNames(candidate, flow, endpoint.FriendlyName, endpoint.ParentName)
                && !HeldByAnother(device, candidate))
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

    /// <summary>Whether another open device of the same flow is bound to the candidate's id.</summary>
    private bool HeldByAnother(HubDevice device, AudioDevice candidate) =>
        _devices.Values.Any(other => !ReferenceEquals(other, device)
            && !other.Closed
            && other.Endpoint.Flow == device.Endpoint.Flow
            && string.Equals(other.Endpoint.Id, candidate.EndpointId, StringComparison.OrdinalIgnoreCase));

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
