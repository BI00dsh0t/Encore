using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Library;
using Encore.Core.Sessions;
using Encore.Engine.Hosting;
using Encore.Engine.Ipc;
using Encore.Engine.Settings;
using Encore.Engine.Supervision;

namespace Encore.Engine;

/// <summary>The running engine for <c>serve</c>: owns what start-up built and stops it in order. See docs/code-notes.md.</summary>
internal sealed class EngineHost : IAsyncDisposable
{
    private readonly EngineServices _services;
    private readonly HeartbeatPublisher _heartbeat;
    private readonly EncoderProbe _probe;
    private int _disposed;

    internal EngineHost(EngineServices services, HeartbeatPublisher heartbeat, EncoderProbe probe, Task shutdownRequested)
    {
        _services = services;
        _heartbeat = heartbeat;
        _probe = probe;
        ShutdownRequested = shutdownRequested;
    }

    public IpcServer Server => _services.Server;

    public SettingsStore Settings => _services.Settings;

    public EventHub Hub => _services.Hub;

    public CommandRouter Router => _services.Router;

    public Warnings Warnings => _services.Warnings;

    public AppSupervisor Apps => _services.Apps;

    public SessionOrchestrator Sessions => _services.Sessions;

    /// <summary>The LibraryActor, which a writer tells about each file it writes.</summary>
    internal ILibraryWrites Library => _services.Library;

    public string PipeName => Server.PipeName;

    /// <summary>Completes once <c>engine.shutdown</c> has been answered.</summary>
    public Task ShutdownRequested { get; }

    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.IpcPipeInUse"/>: another engine serves this instance.</exception>
    public static EngineHost Start(
        Instance instance,
        ILoggerFactory loggers,
        IpcServerOptions? options = null,
        EngineHostOptions? hostOptions = null) =>
        new EngineStartup(instance, loggers, options ?? new IpcServerOptions(), hostOptions ?? new EngineHostOptions()).Start();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _heartbeat.EndAsync().ConfigureAwait(false);
        await _probe.EndAsync().ConfigureAwait(false);
        await _services.DisposeAsync().ConfigureAwait(false);
        _heartbeat.Dispose();
        _probe.Dispose();
    }
}
