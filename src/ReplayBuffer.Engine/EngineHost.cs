using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Engine.Ipc;
using ReplayBuffer.Engine.Settings;

namespace ReplayBuffer.Engine;

/// <summary>
/// The engine's composition root for <c>serve</c>: claims the instance's pipe, opens the settings
/// store, and serves its commands and events until <c>engine.shutdown</c>.
/// </summary>
internal sealed class EngineHost : IAsyncDisposable
{
    /// <summary>A command that never replies, registered only for test instances, so a test can hold one pending.</summary>
    internal static readonly CommandType<Empty, Empty> DebugHold = new("debug.hold", EngineJsonContext.Default.Empty, EngineJsonContext.Default.Empty);

    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private EngineHost(IpcServer server, SettingsStore settings, EventHub hub, CommandRouter router, Warnings warnings)
    {
        Server = server;
        Settings = settings;
        Hub = hub;
        Router = router;
        Warnings = warnings;
    }

    public IpcServer Server { get; }

    public SettingsStore Settings { get; }

    public EventHub Hub { get; }

    public CommandRouter Router { get; }

    public Warnings Warnings { get; }

    public string PipeName => Server.PipeName;

    /// <summary>Completes once <c>engine.shutdown</c> has been answered.</summary>
    public Task ShutdownRequested => _shutdown.Task;

    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.IpcPipeInUse"/>: another engine serves this instance.</exception>
    public static EngineHost Start(Instance instance, ILoggerFactory loggers, IpcServerOptions? options = null)
    {
        var log = loggers.CreateLogger("Ipc");

        // The pipe first: a second engine must fail before it touches the settings file.
        var server = new IpcServer(Contracts.Ipc.PipeName.ForCurrentUser(instance.Name), options ?? new IpcServerOptions(), log);
        try
        {
            var load = SettingsStore.Open(instance.RoamingDataFolder);
            var hub = new EventHub(load.Store.Current);
            load.Store.Changed += document => hub.Publish(SettingsMessages.Changed, new SettingsChanged(document.Revision, document));

            var warnings = new Warnings(hub, TimeProvider.System);
            var router = new CommandRouter(log);
            var host = new EngineHost(server, load.Store, hub, router, warnings);

            router.Register(SettingsMessages.Update, (update, _) => ValueTask.FromResult(new SettingsUpdateResult(load.Store.Update(update).Revision)));
            router.Register(FeedbackMessages.Dismiss, (dismiss, _) => ValueTask.FromResult(warnings.Dismiss(dismiss)));
            router.Register(EngineMessages.Shutdown, (_, _) => ValueTask.FromResult(Empty.Value), () => host._shutdown.TrySetResult());
            if (instance.Name.StartsWith(ServeCommand.TestInstancePrefix, StringComparison.Ordinal))
            {
                router.Register(DebugHold, async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return Empty.Value;
                });
            }

            if (load.LoadCode == ErrorCodes.SettingsUnreadable)
            {
                warnings.Raise(
                    ErrorCodes.SettingsUnreadable,
                    WarningSeverity.Error,
                    "settings",
                    "The settings file couldn't be read. It was moved aside and the defaults are in use.",
                    overlay: false);
            }

            server.Start(hub, router);
            return host;
        }
        catch
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
