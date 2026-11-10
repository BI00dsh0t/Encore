using Microsoft.Extensions.Logging;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Audio;

namespace Encore.Engine.Audio;

/// <summary>Keeps the device list: present endpoints merged with every Track Layout's devices, published when it changes.</summary>
internal sealed class DeviceListPublisher : IDisposable
{
    private readonly Lock _lock = new();
    private readonly IAudioEndpointSource _source;
    private readonly Func<SettingsDocument> _settings;
    private readonly Action<AudioDeviceList> _publish;
    private readonly ILogger _log;
    private IReadOnlyList<AudioDevice> _present;
    private AudioDeviceList _current;
    private long _listings;
    private long _storedListing;
    private int _disposed;

    public DeviceListPublisher(IAudioEndpointSource source, Func<SettingsDocument> settings, Action<AudioDeviceList> publish, ILogger log)
    {
        _source = source;
        _settings = settings;
        _publish = publish;
        _log = log;
        _present = [];
        _current = AudioDeviceListBuilder.Build(_present, settings());

        // Subscribe before the first listing, so a change after this listing is still raised.
        _source.EndpointsChanged += OnEndpointsChanged;
        var listing = Interlocked.Increment(ref _listings);
        var present = TryList();
        lock (_lock)
        {
            // The first list is built without publishing; a newer listing already stored wins.
            if (present is not null && listing > _storedListing)
            {
                _storedListing = listing;
                _present = present;
                _current = AudioDeviceListBuilder.Build(present, settings());
            }

            _log.LogInformation("Listed {Count} audio devices", _current.Devices.Count);
        }
    }

    /// <summary>The list as last built, and published if it changed.</summary>
    public AudioDeviceList Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Runs seed with the current list under the lock, so no change is published between the read and the setup.</summary>
    public T Seed<T>(Func<AudioDeviceList, T> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        lock (_lock)
        {
            return seed(_current);
        }
    }

    /// <summary>The endpoints changed: re-lists them, then rebuilds from the current settings.</summary>
    public void OnEndpointsChanged()
    {
        // Listing makes COM calls; outside the lock. A listing older than one already stored is dropped.
        var listing = Interlocked.Increment(ref _listings);
        var present = TryList();
        if (present is null)
        {
            return;
        }

        lock (_lock)
        {
            if (listing < _storedListing)
            {
                return;
            }

            _storedListing = listing;
            _present = present;
            Rebuild(_settings());
        }
    }

    /// <summary>The settings changed: rebuilds from the cached present endpoints and <paramref name="document"/>.</summary>
    public void OnSettingsChanged(SettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_lock)
        {
            Rebuild(document);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _source.EndpointsChanged -= OnEndpointsChanged;
        }
    }

    private void Rebuild(SettingsDocument document)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var next = AudioDeviceListBuilder.Build(_present, document);
        if (next.Equals(_current))
        {
            return;
        }

        _current = next;
        _log.LogInformation("The audio device list changed: {Count} devices", next.Devices.Count);
        _publish(next);
    }

    private IReadOnlyList<AudioDevice>? TryList()
    {
        try
        {
            return _source.ListPresent();
        }
        catch (Exception e)
        {
            _log.LogError(e, "Couldn't list the audio devices; keeping the last list");
            return null;
        }
    }
}
