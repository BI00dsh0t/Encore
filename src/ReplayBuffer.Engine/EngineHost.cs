using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Hotkeys;
using ReplayBuffer.Engine.Ipc;
using ReplayBuffer.Engine.Sessions;
using ReplayBuffer.Engine.Settings;
using ReplayBuffer.Engine.Supervision;
using ReplayBuffer.Platform.Audio;

namespace ReplayBuffer.Engine;

/// <summary>The engine host's own options; tests turn the heartbeat off and fake the App.</summary>
internal sealed record EngineHostOptions
{
    /// <summary>The <c>engine.heartbeat</c> interval (AD-16), or <c>null</c> for none.</summary>
    public TimeSpan? Heartbeat { get; init; } = Contracts.Supervision.HeartbeatInterval;

    /// <summary>Whether the encoders are probed at startup and <c>capture.videoOptions</c> published; tests that count events turn it off.</summary>
    public bool ProbeEncoders { get; init; } = true;

    /// <summary>Opens and starts the App; <c>null</c> for <c>ReplayBuffer.App.exe</c> beside the engine.</summary>
    public IAppLauncher? AppLauncher { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The engine's composition root for <c>serve</c>: claims the instance's pipe, opens the settings
/// store, and serves its commands and events until <c>engine.shutdown</c>. It publishes the
/// heartbeat and supervises the App that attaches (AD-16). It composes the SessionOrchestrator
/// (AD-19), which publishes <c>capture.stateChanged</c> through the hub; nothing starts a session yet.
/// It registers the Hotkeys (AD-11) before the pipe accepts connections; the orchestrator publishes
/// each press as <c>hotkey.pressed</c>, and no action is bound to one yet. Once the pipe serves, it
/// probes the video encoders off that path and publishes the result as <c>capture.videoOptions</c>.
/// </summary>
internal sealed class EngineHost : IAsyncDisposable
{
    /// <summary>A command that never replies, registered only for test instances, so a test can hold one pending.</summary>
    internal static readonly CommandType<Empty, Empty> DebugHold = new("debug.hold", EngineJsonContext.Default.Empty, EngineJsonContext.Default.Empty);

    /// <summary>
    /// Test instances only: stops the heartbeat and never replies, so the engine looks hung to the
    /// App while its process stays alive.
    /// </summary>
    internal static readonly CommandType<Empty, Empty> DebugHang = new("debug.hang", EngineJsonContext.Default.Empty, EngineJsonContext.Default.Empty);

    /// <summary>
    /// Test instances only: after its reply, ends every connection, the App's included, as a writer-queue
    /// drop would; with <see cref="DebugDrop.RefuseNew"/> the engine also stops accepting connections,
    /// so it stays alive but unreachable.
    /// </summary>
    internal static readonly CommandType<DebugDrop, Empty> DebugDropCommand = new("debug.drop", DebugJsonContext.Default.DebugDrop, DebugJsonContext.Default.Empty);

    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopHeartbeat = new();
    private readonly CancellationTokenSource _stopProbe = new();
    private Task _heartbeat = Task.CompletedTask;
    private Task _probe = Task.CompletedTask;
    private int _disposed;

    private EngineHost(
        IpcServer server,
        SettingsStore settings,
        EventHub hub,
        CommandRouter router,
        Warnings warnings,
        AppSupervisor apps,
        SessionOrchestrator sessions,
        HotkeyService hotkeys,
        AudioDeviceHub audio)
    {
        Audio = audio;
        Server = server;
        Settings = settings;
        Hub = hub;
        Router = router;
        Warnings = warnings;
        Apps = apps;
        Sessions = sessions;
        Hotkeys = hotkeys;
    }

    public IpcServer Server { get; }

    public SettingsStore Settings { get; }

    public EventHub Hub { get; }

    public CommandRouter Router { get; }

    public Warnings Warnings { get; }

    public AppSupervisor Apps { get; }

    public SessionOrchestrator Sessions { get; }

    public HotkeyService Hotkeys { get; }

    /// <summary>The engine's one audio hub (AD-20); disposed last, after every session has ended.</summary>
    public AudioDeviceHub Audio { get; }

    public string PipeName => Server.PipeName;

    /// <summary>Completes once <c>engine.shutdown</c> has been answered.</summary>
    public Task ShutdownRequested => _shutdown.Task;

    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.IpcPipeInUse"/>: another engine serves this instance.</exception>
    public static EngineHost Start(Instance instance, ILoggerFactory loggers, IpcServerOptions? options = null, EngineHostOptions? hostOptions = null)
    {
        hostOptions ??= new EngineHostOptions();
        var log = loggers.CreateLogger("Ipc");
        var hotkeyLog = loggers.CreateLogger("Hotkeys");

        // The pipe first: a second engine must fail before it touches the settings file.
        var server = new IpcServer(Contracts.Ipc.PipeName.ForCurrentUser(instance.Name), options ?? new IpcServerOptions(), log);
        SessionOrchestrator? sessions = null;
        HotkeyService? hotkeys = null;
        var audio = new AudioDeviceHub(loggers);
        try
        {
            var load = SettingsStore.Open(instance.RoamingDataFolder);
            var hub = new EventHub(load.Store.Current);
            load.Store.Changed += document => hub.Publish(SettingsMessages.Changed, new SettingsChanged(document.Revision, document));

            var warnings = new Warnings(hub, hostOptions.Time);
            var router = new CommandRouter(log);
            var apps = new AppSupervisor(
                hub,
                warnings,
                hostOptions.AppLauncher ?? AppLauncher.Beside(instance),
                hostOptions.Time,
                loggers.CreateLogger("Supervision"));
            var encoders = new VideoEncoderCatalog(loggers);
            sessions = new SessionOrchestrator(
                new CaptureSessionFactory(
                    new CaptureSessionOptions { OutputFolder = Path.Combine(instance.LocalDataFolder, "sessions") },
                    loggers,
                    audio,
                    encoders),
                () => load.Store.Current,
                state => hub.Publish(CaptureMessages.StateChanged, state),
                press =>
                {
                    hub.Publish(HotkeyMessages.Pressed, press);
                    HotkeyService.LogPress(hotkeyLog, press);
                });

            // AD-16: only the installed instance falls back to the catalog's default Hotkeys.
            hotkeys = new HotkeyService(load.Store, hub, sessions, instance.Name == Instance.InstalledName, hotkeyLog);
            var host = new EngineHost(server, load.Store, hub, router, warnings, apps, sessions, hotkeys, audio);

            router.Register(SettingsMessages.Update, (update, _) => ValueTask.FromResult(new SettingsUpdateResult(load.Store.Update(update).Revision)));
            router.Register(FeedbackMessages.Dismiss, (dismiss, _) => ValueTask.FromResult(warnings.Dismiss(dismiss)));
            router.Register(EngineMessages.Attach, (attach, _) => ValueTask.FromResult(apps.Attach(attach)));
            router.Register(
                EngineMessages.Shutdown,
                (_, _) =>
                {
                    // Before the reply, so the App can't exit and be relaunched in between.
                    apps.Stop();
                    return ValueTask.FromResult(Empty.Value);
                },
                () => host._shutdown.TrySetResult());
            if (instance.IsTest)
            {
                router.Register(DebugHold, async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return Empty.Value;
                });
                router.Register(
                    DebugDropCommand,
                    async (drop, _) =>
                    {
                        if (drop.RefuseNew)
                        {
                            await server.StopAcceptingAsync().ConfigureAwait(false);
                        }

                        return Empty.Value;
                    },
                    () => _ = Task.Delay(100).ContinueWith(_ => server.DropAll("debug.drop"), TaskScheduler.Default));
                router.Register(DebugHang, async (_, cancellationToken) =>
                {
                    await host._stopHeartbeat.CancelAsync().ConfigureAwait(false);
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
            if (hostOptions.ProbeEncoders)
            {
                host._probe = host.ProbeAsync(encoders);
            }
            if (hostOptions.Heartbeat is { } interval)
            {
                host._heartbeat = host.HeartbeatAsync(interval, hostOptions.Time);
            }

            return host;
        }
        catch
        {
            hotkeys?.Dispose();
            sessions?.Dispose();
            audio.Dispose();
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopHeartbeat.CancelAsync().ConfigureAwait(false);
        await _heartbeat.ConfigureAwait(false);

        // A probe stops between codecs; the one under way finishes first.
        await _stopProbe.CancelAsync().ConfigureAwait(false);
        await _probe.ConfigureAwait(false);
        Apps.Dispose();

        // No more presses are posted to the orchestrator once this returns.
        Hotkeys.Dispose();

        // Ends a running session, finishing its file, and joins the orchestrator thread.
        Sessions.Dispose();
        await Server.DisposeAsync().ConfigureAwait(false);
        Audio.Dispose();
        _stopHeartbeat.Dispose();
        _stopProbe.Dispose();
    }

    /// <summary>
    /// Probes the encoders on a pool thread and publishes <c>capture.videoOptions</c> once (R2).
    /// A failed probe publishes probed options with no codecs; a cancelled one publishes nothing.
    /// </summary>
    private async Task ProbeAsync(VideoEncoderCatalog encoders)
    {
        var token = _stopProbe.Token;
        try
        {
            var options = await Task.Run(() => encoders.ProbeAll(token), token).ConfigureAwait(false);
            Hub.Publish(CaptureMessages.VideoOptionsChanged, options);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    /// <summary>Publishes <c>engine.heartbeat</c> every <paramref name="interval"/> until stopped (AD-16).</summary>
    private async Task HeartbeatAsync(TimeSpan interval, TimeProvider time)
    {
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopHeartbeat.Token).ConfigureAwait(false))
            {
                Hub.Publish(EngineMessages.Heartbeat, new EngineHeartbeat(time.GetUtcNow()));
            }
        }
        catch (OperationCanceledException) when (_stopHeartbeat.IsCancellationRequested)
        {
        }
    }
}

/// <summary><c>debug.drop</c>'s arguments.</summary>
internal sealed record DebugDrop(bool RefuseNew);

[JsonSourceGenerationOptions(JsonSerializerDefaults.General, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DebugDrop))]
[JsonSerializable(typeof(Empty))]
internal sealed partial class DebugJsonContext : JsonSerializerContext;
