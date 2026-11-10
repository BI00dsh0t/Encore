using Microsoft.Extensions.Logging;
using Encore.Contracts.Messages;
using Encore.Core.Audio;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Platform.Audio;

/// <summary>This PC's audio endpoints: lists the active ones of both flows, and starts a watcher for the first change handler.</summary>
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
        [.. List(AudioFlow.Output, SettingsFlow.Output), .. List(AudioFlow.Input, SettingsFlow.Input)];

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

    /// <summary>One flow's active endpoints, with its Windows default (console role) marked.</summary>
    private static IEnumerable<AudioDevice> List(AudioFlow flow, SettingsFlow settingsFlow)
    {
        var endpoints = AudioEndpoints.List(flow);
        var defaultId = endpoints.Count > 0 ? AudioEndpoints.DefaultId(flow) : null;
        return endpoints.Select(endpoint => new AudioDevice(
            endpoint.Id,
            endpoint.FriendlyName,
            endpoint.ParentName,
            settingsFlow,
            Available: true,
            IsDefault: string.Equals(endpoint.Id, defaultId, StringComparison.OrdinalIgnoreCase)));
    }

    // On the watcher's thread; reads the handlers without the gate, so disposal can join it.
    private void RaiseEndpointsChanged() => Volatile.Read(ref _endpointsChanged)?.Invoke();
}
