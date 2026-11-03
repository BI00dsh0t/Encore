using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Audio;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Platform.Audio;

/// <summary>
/// This PC's MMDevice endpoints (R1): lists the active ones of both flows, and the first
/// <see cref="EndpointsChanged"/> handler starts an <see cref="EndpointWatcher"/>, which runs until disposal.
/// </summary>
internal sealed class WindowsEndpointSource(ILogger log) : IAudioEndpointSource, IDisposable
{
    private readonly Lock _gate = new();
    private bool _disposed;
    private Action? _endpointsChanged;
    private EndpointWatcher? _watcher;

    /// <inheritdoc/>
    public event Action? EndpointsChanged
    {
        add
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _endpointsChanged += value;
                _watcher ??= new EndpointWatcher(RaiseEndpointsChanged, log);
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
    public EndpointWatcher? Watcher
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
}
