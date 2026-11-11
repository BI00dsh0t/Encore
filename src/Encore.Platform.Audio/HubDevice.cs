using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Core.Audio;
using Encore.Media;

namespace Encore.Platform.Audio;

/// <summary>One open endpoint: its client, or a silence hold while dropped; these change only under the hub's gate.</summary>
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
        var frequency = Stopwatch.Frequency;
        Diagnostics = new AudioDeviceDiagnostics(client.Endpoint, new AudioStampStats(frequency), new DeviceStream(frequency, this));
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

    /// <summary>Whether this dropout continues one already reported, so its resume is quiet. Under the hub's gate.</summary>
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
