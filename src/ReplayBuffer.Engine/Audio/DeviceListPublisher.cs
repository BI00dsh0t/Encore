using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Audio;

namespace ReplayBuffer.Engine.Audio;

/// <summary>
/// Keeps the device list (R1): the present endpoints merged with every Track Layout's devices
/// (<see cref="AudioDeviceListBuilder"/>). It re-lists when the endpoints change and rebuilds when
/// the settings do, publishing the list only when it differs from the last one. The first list is
/// built without publishing, for the initial snapshot. A failed listing is logged and keeps the
/// previous present endpoints.
/// </summary>
internal sealed class DeviceListPublisher : IDisposable
{
    private readonly Lock _lock = new();
    private readonly IAudioEndpointSource _source;
    private readonly Func<SettingsDocument> _settings;
    private readonly Action<AudioDeviceList> _publish;
    private readonly ILogger _log;
    private IReadOnlyList<AudioDevice> _present;
    private AudioDeviceList _current;
    private int _disposed;

    public DeviceListPublisher(IAudioEndpointSource source, Func<SettingsDocument> settings, Action<AudioDeviceList> publish, ILogger log)
    {
        _source = source;
        _settings = settings;
        _publish = publish;
        _log = log;
        _present = TryList() ?? [];
        _current = AudioDeviceListBuilder.Build(_present, settings());
        _log.LogInformation("Listed {Count} audio devices", _current.Devices.Count);
        _source.EndpointsChanged += OnEndpointsChanged;
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

    /// <summary>
    /// Runs <paramref name="seed"/> with <see cref="Current"/> under the lock, so no change is
    /// published between reading the list and what <paramref name="seed"/> sets up to receive the next one.
    /// </summary>
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
        // Listing makes COM calls; outside the lock.
        var present = TryList();
        if (present is null)
        {
            return;
        }

        lock (_lock)
        {
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
