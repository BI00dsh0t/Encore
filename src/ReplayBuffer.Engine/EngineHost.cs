using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Audio;
using ReplayBuffer.Core.Feedback;
using ReplayBuffer.Core.Library;
using ReplayBuffer.Core.Output;
using ReplayBuffer.Core.Replay;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Core.Settings;
using ReplayBuffer.Engine.Audio;
using ReplayBuffer.Engine.Hotkeys;
using ReplayBuffer.Engine.Ipc;
using ReplayBuffer.Engine.Sessions;
using ReplayBuffer.Engine.Settings;
using ReplayBuffer.Engine.Supervision;
using ReplayBuffer.Platform.Audio;
using ReplayBuffer.Platform.FFmpeg;
using ReplayBuffer.Platform.Windows;

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

    /// <summary>The PC's audio endpoints for the device list; <c>null</c> for the engine's <see cref="AudioDeviceHub"/>.</summary>
    public IAudioEndpointSource? AudioEndpoints { get; init; }

    /// <summary>The hub the Track meters subscribe to; <c>null</c> for the engine's <see cref="AudioDeviceHub"/>.</summary>
    public IAudioDeviceHub? MeterAudio { get; init; }

    /// <summary>
    /// The foreground window probe of Game Detection; <c>null</c> for the real one. Injecting one turns
    /// detection on for a test instance, where it is otherwise off unless <see cref="DebugCapture"/> is set.
    /// </summary>
    public IGameWindowProbe? GameWindows { get; init; }

    /// <summary><c>serve --debug-capture</c>: a capture session follows the Target Game.</summary>
    public bool DebugCapture { get; init; }

    /// <summary>
    /// Whether a Replay Buffer session follows the Target Game and a <c>save</c> press writes a
    /// Clip (R1, R4); null is on for every instance but a test one. <see cref="DebugCapture"/> wins.
    /// </summary>
    public bool? ReplayBuffer { get; init; }

    /// <summary>The time between Game Detection polls.</summary>
    public TimeSpan GameScanInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Where Feedback Sounds play; <c>null</c> for WASAPI on a non-test instance, and a silent
    /// output on a test one.
    /// </summary>
    public IFeedbackSoundOutput? FeedbackSounds { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The engine's composition root for <c>serve</c>: claims the instance's pipe, opens the settings
/// store, and serves its commands and events until <c>engine.shutdown</c>. It publishes the
/// heartbeat and supervises the App that attaches (AD-16). It composes the SessionOrchestrator
/// (AD-19), which publishes <c>capture.stateChanged</c> through the hub; a session starts only on the Target Game.
/// The orchestrator owns mic mute, kept in the instance's <c>runtime.json</c> (AD-5) and loaded
/// before the pipe serves. It registers the Hotkeys (AD-11) before the pipe accepts connections; the
/// orchestrator publishes each press as <c>hotkey.pressed</c>, and a <c>micMute</c> press also toggles
/// mic mute, a <c>save</c> press saves a Clip, and a <c>recordToggle</c> press starts or stops a
/// Recording beside the Replay Buffer, published as <c>recording.started</c> and
/// <c>recording.stopped</c>. Once the pipe serves, it
/// probes the video encoders off that path and publishes the result as <c>capture.videoOptions</c>.
/// It lists the audio devices before the hub exists, so the first snapshot holds them, and then
/// publishes <c>devices.changed</c> when the endpoints or the Track Layouts change them (R1).
/// Each audio device dropout the hub reports raises a <c>device-dropout</c> warning (R5).
/// <c>meters.watch</c> meters a Track Layout on the hub with its own mixer, nothing captured, and
/// publishes <c>meters.levels</c> until <c>meters.unwatch</c> or the next watch; the orchestrator's
/// mic mute reaches the meters as it does a session (R6).
/// Game Detection proposes the Target Game to the orchestrator, which publishes <c>target.changed</c>;
/// a Replay Buffer session follows the Target, its audio from the snapshot's Track Layout, and a
/// <c>save</c> press writes a Clip, published as <c>clip.saved</c>, or one too soon after the last
/// Reset or the session's start is refused as <c>clip.failed</c>. With <c>--debug-capture</c> a
/// capture session writing one file follows the Target instead.
/// The FeedbackRouter turns Clips saved and not saved, Recording state changes and a disk full into
/// <c>warning.raised</c> notices, and dismisses the info ones after 10 s. <c>dev.simulate</c> injects a
/// stub Recording state, a disk full, a save failure or a saved notice; the installed instance refuses it.
/// Each notice's Feedback Sound, if switched on, plays on a worker on <c>feedbackSoundDevice</c> or
/// the default communications endpoint; <c>feedback.preview</c> plays one even when it is off. While
/// a Track of the active Track Layout mixes that endpoint, a <c>feedback-device-captured</c> warning
/// is active, re-checked on settings, endpoint and target changes.
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
    private GameMonitor? _games;
    private LibraryActor _library = null!;
    private FeedbackRouter? _feedback;
    private FeedbackDeviceCheck? _feedbackCheck;
    private MemoryMarginReporter? _marginReporter;
    private EstimatePublisher? _estimates;
    private FeedbackSoundPlayer? _sounds;
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
        DeviceListPublisher devices,
        DeviceDropoutReporter dropouts,
        TrackMeters meters,
        AudioDeviceHub audio)
    {
        Meters = meters;
        Devices = devices;
        Dropouts = dropouts;
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

    /// <summary>The device list (R1), served by <c>devices.list</c> and published as <c>devices.changed</c>.</summary>
    public DeviceListPublisher Devices { get; }

    /// <summary>Raises <c>device-dropout</c> for each audio device dropout (R5).</summary>
    public DeviceDropoutReporter Dropouts { get; }

    /// <summary>The Track meters (R6), served by <c>meters.watch</c> and <c>meters.unwatch</c>.</summary>
    public TrackMeters Meters { get; }

    /// <summary>The engine's one audio hub (AD-20); disposed last, after every session has ended.</summary>
    public AudioDeviceHub Audio { get; }

    /// <summary>The LibraryActor, which a writer tells about each file it writes (R6).</summary>
    internal ILibraryWrites Library => _library;

    public string PipeName => Server.PipeName;

    /// <summary>Completes once <c>engine.shutdown</c> has been answered.</summary>
    public Task ShutdownRequested => _shutdown.Task;

    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.IpcPipeInUse"/>: another engine serves this instance.</exception>
    public static EngineHost Start(Instance instance, ILoggerFactory loggers, IpcServerOptions? options = null, EngineHostOptions? hostOptions = null)
    {
        hostOptions ??= new EngineHostOptions();
        var log = loggers.CreateLogger("Ipc");
        var hotkeyLog = loggers.CreateLogger("Hotkeys");
        var gameLog = loggers.CreateLogger("Games");
        var clipLog = loggers.CreateLogger("Clips");
        var recordingLog = loggers.CreateLogger("Recordings");
        var libraryLog = loggers.CreateLogger("Library");
        var ffmpegLog = loggers.CreateLogger("FFmpeg");
        var replay = !hostOptions.DebugCapture && (hostOptions.ReplayBuffer ?? !instance.IsTest);

        // The pipe first: a second engine must fail before it touches the settings file.
        var server = new IpcServer(Contracts.Ipc.PipeName.ForCurrentUser(instance.Name), options ?? new IpcServerOptions(), log);
        SessionOrchestrator? sessions = null;
        HotkeyService? hotkeys = null;
        DeviceListPublisher? devices = null;
        DeviceDropoutReporter? dropouts = null;
        MemoryMarginReporter? marginReporter = null;
        EstimatePublisher? estimates = null;
        TrackMeters? meters = null;
        GameMonitor? games = null;
        LibraryActor? library = null;
        FeedbackRouter? feedback = null;
        FeedbackSoundPlayer? sounds = null;
        FeedbackDeviceCheck? feedbackCheck = null;
        var audio = new AudioDeviceHub(loggers);
        try
        {
            var load = SettingsStore.Open(instance.RoamingDataFolder);

            // The Library at start seeds the hub's initial state, as the device list does.
            string OutputFolder() =>
                SettingsResolver.Resolve(load.Store.Current, null).OutputFolder
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), instance.Name);
            // FR-12: the Recordings the last run left open are trimmed before the Library sees them.
            var runtime = new RuntimeStateStore(instance.LocalDataFolder, loggers.CreateLogger("Runtime"));
            var interrupted = InterruptedRecordings.Recover(runtime.ReadInterrupted(), recordingLog);

            var libraryFile = Path.Combine(instance.RoamingDataFolder, "library.json");

            // A top-level folder's Game Profile, for files found on disk (AD-21).
            string? ProfileForFolder(string folder) =>
                load.Store.Current.Profiles.FirstOrDefault(p => string.Equals(p.Value.Folder, folder, StringComparison.OrdinalIgnoreCase)).Key;
            var libraryLoad = LibraryActor.Load(
                OutputFolder(),
                libraryFile,
                ProfileForFolder,
                e => libraryLog.LogWarning("The Library couldn't be read in full: {Message}", e.Message));

            // The device list seeds the hub's initial state; a change is published once the hub exists.
            EventHub? publishTo = null;
            devices = new DeviceListPublisher(
                hostOptions.AudioEndpoints ?? audio,
                () => load.Store.Current,
                list => Volatile.Read(ref publishTo)?.Publish(AudioMessages.Changed, list),
                loggers.CreateLogger("Audio"));
            // The storage estimate (R2) seeds the initial state likewise.
            estimates = new EstimatePublisher(
                () => load.Store.Current,
                new SystemMemoryStatus(),
                EstimatePublisher.FreeDiskBytes,
                Path.Combine(instance.LocalDataFolder, "ring"),
                e => Volatile.Read(ref publishTo)?.Publish(CaptureMessages.EstimateChanged, e),
                hostOptions.Time);
            var estimate = estimates.Seed();
            var libraryFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutputFolder()));
            var hub = devices.Seed(list => publishTo = new EventHub(load.Store.Current, list, libraryLoad.Items, libraryLoad.Corrupt, libraryFolder, estimate));
            load.Store.Changed += document => hub.Publish(SettingsMessages.Changed, new SettingsChanged(document.Revision, document));
            load.Store.Changed += devices.OnSettingsChanged;
            load.Store.Changed += estimates.OnSettingsChanged;

            // Every commit re-resolves the game session's settings (R7, R8), once the orchestrator exists.
            SessionOrchestrator? settingsTo = null;
            load.Store.Changed += _ => Volatile.Read(ref settingsTo)?.Post(new SettingsUpdated());

            var libraryActor = library = new LibraryActor(
                libraryLoad.Items,
                OutputFolder,
                libraryFile,
                ProfileForFolder,
                libraryLoad.Corrupt,
                item => hub.Publish(LibraryMessages.ItemAdded, item),
                item => hub.Publish(LibraryMessages.ItemChanged, item),
                removed => hub.Publish(LibraryMessages.ItemRemoved, removed),
                reset => hub.Publish(LibraryMessages.Reset, reset),
                blocked => hub.Publish(LibraryMessages.CleanupBlockedChanged, blocked),
                hostOptions.Time,
                e => libraryLog.LogWarning("A Library disk operation failed: {Message}", e.Message),
                createMissingFolder: !instance.IsTest,
                readLength: path => MediaLength.Read(path, ffmpegLog),
                folderChanged: changed => hub.Publish(LibraryMessages.FolderChanged, changed),
                recycle: RecycleBin.Recycle);

            // Runs on the updating thread, so it only posts; the actor ignores a change that keeps the folder.
            load.Store.Changed += _ => libraryActor.OutputFolderChanged();
            string? corruptWarning = null;
            var warnings = new Warnings(hub, hostOptions.Time);
            var feedbackLog = loggers.CreateLogger("Feedback");
            var player = sounds = new FeedbackSoundPlayer(
                hostOptions.FeedbackSounds ?? (instance.IsTest ? SilentFeedbackSoundOutput.Instance : new FeedbackSoundOutput()),
                () => load.Store.Current,
                (message, e) => feedbackLog.LogWarning(e, "{Message}", message));
            var notices = feedback = new FeedbackRouter(warnings, hostOptions.Time, player);
            var soundCheck = feedbackCheck = new FeedbackDeviceCheck(
                player,
                load.Store,
                hostOptions.AudioEndpoints ?? audio,
                () => hub.State.TargetGame?.ProfileId,
                warnings,
                feedbackLog);
            Action<ClipFailed> notSaved = failed =>
            {
                hub.Publish(CaptureMessages.Failed, failed);
                clipLog.LogInformation("The save made no Clip: {Reason}", failed.Reason);
                notices.ClipNotSaved(failed);
            };
            dropouts = new DeviceDropoutReporter(audio, warnings);

            // One margin for the engine: every session and ring generation shares it, so neither re-arms the warning.
            var margin = new MemoryMargin(new SystemMemoryStatus());
            marginReporter = new MemoryMarginReporter(margin, warnings);
            var router = new CommandRouter(log);
            var apps = new AppSupervisor(
                hub,
                warnings,
                hostOptions.AppLauncher ?? AppLauncher.Beside(instance),
                hostOptions.Time,
                loggers.CreateLogger("Supervision"));
            var encoders = new VideoEncoderCatalog(loggers);
            var trackMeters = meters = new TrackMeters(
                hostOptions.MeterAudio ?? audio,
                levels => hub.Publish(AudioMessages.Levels, levels),
                loggers.CreateLogger("Audio"));
            // Ring files a killed engine left (DeleteOnClose covers a crash, not a power loss), before any session makes one.
            var ringFolder = Path.Combine(instance.LocalDataFolder, "ring");
            var stale = RingFile.RemoveStale(ringFolder);
            if (SettingsResolver.Resolve(load.Store.Current, null).RingPath is { Length: > 0 } ringPath)
            {
                stale += RingFile.RemoveStale(ringPath);
            }

            if (stale > 0)
            {
                clipLog.LogInformation("Removed {Count} stale Replay Buffer ring files", stale);
            }

            var defaultOutputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), instance.Name);
            sessions = new SessionOrchestrator(
                new CaptureSessionFactory(
                    new CaptureSessionOptions
                    {
                        OutputFolder = Path.Combine(instance.LocalDataFolder, "sessions"),
                        Replay = replay,
                        RingFolder = ringFolder,
                        Margin = margin,
                        ResetGap = message => warnings.Raise(ErrorCodes.ResetGap, WarningSeverity.Warning, "replay", message, overlay: false),
                        CaptureFallback = message => warnings.Raise(ErrorCodes.MonitorCaptureFallback, WarningSeverity.Warning, "recordings", message, overlay: false),
                    },
                    loggers,
                    audio,
                    encoders),
                () => load.Store.Current,
                state =>
                {
                    hub.Publish(CaptureMessages.StateChanged, state);
                    trackMeters.SetMicMuted(state.MicMuted);
                    notices.StateChanged(state);
                },
                press =>
                {
                    hub.Publish(HotkeyMessages.Pressed, press);
                    HotkeyService.LogPress(hotkeyLog, press);
                },
                runtime,
                changed =>
                {
                    hub.Publish(GameMessages.TargetChanged, changed);
                    soundCheck.Check();
                },
                hostOptions.DebugCapture
                    ? new TargetCapture(
                        _ => SessionAudio.None,
                        (target, e) => gameLog.LogWarning(e, "The debug capture of {Name} (process {Pid}) failed", target.Name, target.ProcessId))
                    : replay
                        ? new TargetCapture(
                            snapshot => SessionAudio.FromLayout(snapshot.TrackLayout),
                            (target, e) => gameLog.LogWarning(e, "The Replay Buffer of {Name} (process {Pid}) failed", target.Name, target.ProcessId))
                        : null,
                replay
                    ? new ReplayClips(
                        OutputPathAllocator.System,
                        () => defaultOutputFolder,
                        saved =>
                        {
                            hub.Publish(CaptureMessages.Saved, saved);
                            clipLog.LogInformation("Clip saved to {Path}", saved.Path);
                            notices.ClipSaved(saved);
                        },
                        e => clipLog.LogWarning("The save failed: {Message}", e.Message),
                        notSaved,
                        Saving: saving =>
                        {
                            hub.Publish(CaptureMessages.Saving, saving);
                            clipLog.LogDebug("Saving a Clip");
                        },
                        Ignored: since => clipLog.LogInformation(
                            "The save press came {Seconds:0.0} s after the last accepted one, inside the Save Cooldown; it was ignored", since.TotalSeconds),
                        Library: libraryActor)
                    : null,
                replay
                    ? new Recordings(
                        OutputPathAllocator.System,
                        () => defaultOutputFolder,
                        started =>
                        {
                            hub.Publish(RecordingMessages.Started, started);
                            recordingLog.LogInformation("Recording to {Path}", started.Path);
                            notices.RecordingStarted(started);
                        },
                        stopped =>
                        {
                            hub.Publish(RecordingMessages.Stopped, stopped);
                            recordingLog.LogInformation("Recording stopped ({Reason}): {Path}", stopped.Reason, stopped.Path);
                            notices.RecordingStopped(stopped);
                        },
                        e => recordingLog.LogWarning("The Recording failed: {Message}", e.Message),
                        libraryActor,
                        message => recordingLog.LogInformation("{Message}", message),
                        runtime,
                        Restarted: restart =>
                        {
                            recordingLog.LogWarning("An encoder restart ended the Recording {Stopped}; it continues in {Started}", restart.StoppedPath, restart.StartedPath);
                            notices.RecordingRestarted(restart);
                        },
                        DiskFull: () =>
                        {
                            recordingLog.LogWarning("The Recording's disk is full; the Recording stopped");
                            notices.DiskFull();
                        },
                        RingFits: snapshot => ReplayRings.Fits(snapshot, ringFolder, EstimatePublisher.FreeDiskBytes),
                        ReplayDiskFull: stopped =>
                        {
                            if (stopped)
                            {
                                recordingLog.LogWarning("The disk is full: the Disk-mode Replay Buffer stopped until its ring fits again");
                                notices.ReplayStoppedDiskFull();
                            }
                            else
                            {
                                recordingLog.LogInformation("The wait for room for the Replay Buffer's ring ended");
                                notices.ReplayResumed();
                            }
                        },
                        MonitorStarted: started =>
                        {
                            hub.Publish(RecordingMessages.Started, started);
                            recordingLog.LogInformation("Monitor Recording to {Path}", started.Path);
                            notices.MonitorRecordingStarted(started);
                        },
                        MonitorStopped: stopped =>
                        {
                            hub.Publish(RecordingMessages.Stopped, stopped);
                            recordingLog.LogInformation("Monitor Recording stopped ({Reason}): {Path}", stopped.Reason, stopped.Path);
                            notices.MonitorRecordingStopped(stopped);
                        },
                        Warn: (code, message) =>
                        {
                            recordingLog.LogWarning("{Code}: {Message}", code, message);
                            warnings.Raise(code, WarningSeverity.Warning, "recordings", message, overlay: true);
                        })
                    : null);

            Volatile.Write(ref settingsTo, sessions);

            // Test instances detect games only when a test asks: other tests must never see a stray target.changed.
            if (hostOptions.GameWindows is not null || hostOptions.DebugCapture || replay || !instance.IsTest)
            {
                SessionOrchestrator proposeTo = sessions;
                var listed = ListedGames(new GameConfigStore(gameLog), () => load.Store.Current);
                games = new GameMonitor(
                    hostOptions.GameWindows ?? new ForegroundGameWindowProbe(),
                    pid => pid == Environment.ProcessId || pid == hub.State.AppPid,
                    (to, reason) => proposeTo.Post(new ProposeTarget(to, reason)),
                    hostOptions.GameScanInterval,
                    gameLog,
                    listed,
                    InstallFolder(),
                    game =>
                    {
                        // The profile exists before anything names it (story 3.4).
                        GameProfiles.EnsureProfile(load.Store, game.ProfileId, game.Name, gameLog);
                        hub.Publish(GameMessages.Detected, game);
                    },
                    game => hub.Publish(GameMessages.Exited, game),
                    () => hub.State.TargetGame?.ProcessId,
                    hostOptions.Time,
                    id => load.Store.Current.Profiles.TryGetValue(id, out var profile) && profile.Ignored);
            }

            // AD-16: only the installed instance falls back to the catalog's default Hotkeys.
            hotkeys = new HotkeyService(load.Store, hub, sessions, instance.Name == Instance.InstalledName, hotkeyLog);
            var host = new EngineHost(server, load.Store, hub, router, warnings, apps, sessions, hotkeys, devices, dropouts, trackMeters, audio) { _games = games, _library = libraryActor, _feedback = notices, _feedbackCheck = soundCheck, _sounds = player, _marginReporter = marginReporter, _estimates = estimates };

            router.Register(SettingsMessages.Update, (update, _) => ValueTask.FromResult(new SettingsUpdateResult(load.Store.Update(update).Revision)));
            router.Register(AudioMessages.List, (_, _) => ValueTask.FromResult(devices.Current));
            router.Register(AudioMessages.Watch, (watch, _) =>
            {
                trackMeters.Watch(watch.Layout);
                return ValueTask.FromResult(Empty.Value);
            });
            router.Register(AudioMessages.Unwatch, (_, _) =>
            {
                trackMeters.Unwatch();
                return ValueTask.FromResult(Empty.Value);
            });
            router.Register(FeedbackMessages.Dismiss, (dismiss, _) =>
            {
                var dismissed = warnings.Dismiss(dismiss);

                // Dismissing library-corrupt acknowledges the rebuilt index, so Cleanup may run again.
                if (dismiss.Id == corruptWarning)
                {
                    libraryActor.AcknowledgeCorrupt();
                }

                return ValueTask.FromResult(dismissed);
            });
            router.Register(LibraryMessages.SetFavourite, async (favourite, _) =>
            {
                // Never retried: a stale identity comes back as library-item-changed (AD-3).
                await libraryActor.SetFavourite(favourite).ConfigureAwait(false);
                return Empty.Value;
            });
            router.Register(LibraryMessages.Rename, async (rename, _) =>
            {
                // Never retried: a refusal comes back with its code for the App to show.
                await libraryActor.Rename(rename).ConfigureAwait(false);
                return Empty.Value;
            });
            router.Register(LibraryMessages.Delete, async (delete, _) =>
            {
                await libraryActor.Delete(delete).ConfigureAwait(false);
                return Empty.Value;
            });
            router.Register(FeedbackMessages.Preview, (preview, _) =>
            {
                player.Preview(preview.Sound);
                return ValueTask.FromResult(Empty.Value);
            });
            router.Register(GameMessages.Add, (add, _) => ValueTask.FromResult(ManualGames.Add(load.Store, add)));
            SessionOrchestrator simulateTo = sessions;
            var simulator = new DevSimulator(
                instance,
                running => simulateTo.Post(new RecordingRunning(running)),
                notices.RecordingStarted,
                notices.RecordingStopped,
                notices.DiskFull,
                notSaved,
                () => notices.ClipSaved(new ClipSaved("dev.simulate")));
            router.Register(DevMessages.Simulate, (simulate, _) => ValueTask.FromResult(simulator.Simulate(simulate)));
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

            // AD-16: an interrupted Recording is never resumed, only reported.
            foreach (var message in interrupted)
            {
                warnings.Raise(ErrorCodes.RecordingInterrupted, WarningSeverity.Warning, "recordings", message, overlay: true);
            }

            // Only once they are reported: a start that fails before here reports them next time.
            runtime.ClearInterrupted();

            if (libraryLoad.Corrupt)
            {
                corruptWarning = warnings.Raise(
                    ErrorCodes.LibraryCorrupt,
                    WarningSeverity.Warning,
                    "library",
                    "The Library index was unreadable and was rebuilt from the Output Folder; Favourites may be lost. Cleanup stays off until this is acknowledged.",
                    overlay: false).Id;
            }

            soundCheck.Check();
            server.Start(hub, router);
            games?.Start();
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
            games?.Dispose();
            hotkeys?.Dispose();
            sessions?.Dispose();
            library?.Dispose();
            feedback?.Dispose();
            feedbackCheck?.Dispose();
            sounds?.Dispose();
            meters?.Dispose();
            devices?.Dispose();
            dropouts?.Dispose();
            marginReporter?.Dispose();
            estimates?.Dispose();
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

        // No more presses or proposals are posted to the orchestrator once these return.
        Hotkeys.Dispose();
        _games?.Dispose();

        // Ends a running session, finishing its file, and joins the orchestrator thread.
        Sessions.Dispose();

        // After the orchestrator, so every write it ended has been saved. A write still in flight here gets no EndWrite (4.5 deferred) and is found by the next start scan without its profile.
        _library.Dispose();

        // No more outcomes reach the router once the orchestrator has stopped.
        _feedback?.Dispose();

        // Plays what the router queued, then joins the sound worker.
        _feedbackCheck?.Dispose();
        _sounds?.Dispose();
        await Server.DisposeAsync().ConfigureAwait(false);

        // Closes the watch's subscriptions before the hub goes.
        Meters.Dispose();
        Devices.Dispose();
        Dropouts.Dispose();
        _marginReporter?.Dispose();
        _estimates?.Dispose();
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

    /// <summary>
    /// The listed games for Game Detection: Windows' game list and <c>manualGames</c> (canonical, or lowercase when not on disk),
    /// rebuilt only when either source returns a different instance. Called on the monitor's thread.
    /// </summary>
    private static Func<IReadOnlySet<string>> ListedGames(GameConfigStore windowsList, Func<SettingsDocument> settings)
    {
        IReadOnlySet<string>? fromList = null;
        SettingsDocument? fromSettings = null;
        IReadOnlySet<string> listed = new HashSet<string>(StringComparer.Ordinal);
        return () =>
        {
            var list = windowsList.Paths();
            var document = settings();
            if (!ReferenceEquals(list, fromList) || !ReferenceEquals(document, fromSettings))
            {
                var union = new HashSet<string>(list, StringComparer.Ordinal);
                union.UnionWith(SettingsResolver.Resolve(document, null).ManualGames.Select(GamePaths.CanonicalOrLowercase));
                (fromList, fromSettings, listed) = (list, document, union);
            }

            return listed;
        };
    }

    /// <summary>The engine's install folder, canonical with a trailing <c>\</c>; nothing under it is a game.</summary>
    private static string InstallFolder()
    {
        string folder;
        try
        {
            folder = GamePaths.Canonicalize(AppContext.BaseDirectory);
        }
        catch (Exception e) when (GamePaths.CanonicalizeFailed(e))
        {
            folder = Path.GetFullPath(AppContext.BaseDirectory).ToLowerInvariant();
        }

        return folder.EndsWith('\\') ? folder : folder + '\\';
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
