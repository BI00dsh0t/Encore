using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Feedback;
using Encore.Core.Library;
using Encore.Core.Output;
using Encore.Core.Replay;
using Encore.Core.Sessions;
using Encore.Core.Settings;
using Encore.Engine.Audio;
using Encore.Engine.Hotkeys;
using Encore.Engine.Ipc;
using Encore.Engine.Sessions;
using Encore.Engine.Settings;
using Encore.Engine.Supervision;
using Encore.Platform.Audio;
using Encore.Platform.Capture;
using Encore.Platform.FFmpeg;
using Encore.Platform.Windows;

namespace Encore.Engine.Hosting;

/// <summary>Builds the engine's services for <c>serve</c> in start-up order. See docs/code-notes.md.</summary>
internal sealed class EngineStartup
{
    private readonly Instance _instance;
    private readonly ILoggerFactory _loggers;
    private readonly IpcServerOptions _serverOptions;
    private readonly EngineHostOptions _options;
    private readonly bool _replay;
    private readonly StartupCleanup _cleanup = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger _ipcLog;
    private readonly ILogger _gameLog;
    private readonly ILogger _clipLog;
    private readonly ILogger _recordingLog;
    private readonly ILogger _libraryLog;

    // Read late by handlers that subscribe before these exist; null until built.
    private EventHub? _hubOnceBuilt;
    private SessionOrchestrator? _sessionsOnceBuilt;

    private IpcServer _server = null!;
    private AudioDeviceHub _audio = null!;
    private SettingsLoad _settings = null!;
    private RuntimeStateStore _runtime = null!;
    private IReadOnlyList<string> _interrupted = [];
    private string _libraryFile = "";
    private LibraryLoad _libraryLoad = null!;
    private DeviceListPublisher _devices = null!;
    private EstimatePublisher _estimates = null!;
    private EventHub _hub = null!;
    private Warnings _warnings = null!;
    private LibraryActor _library = null!;
    private FeedbackSoundPlayer _sounds = null!;
    private FeedbackRouter _feedback = null!;
    private FeedbackDeviceCheck _feedbackCheck = null!;
    private DeviceDropoutReporter _dropouts = null!;
    private MemoryMargin _margin = null!;
    private MemoryMarginReporter _marginReporter = null!;
    private CommandRouter _router = null!;
    private AppSupervisor _apps = null!;
    private VideoEncoderCatalog _encoders = null!;
    private TrackMeters _meters = null!;
    private CaptureOutcomes _outcomes = null!;
    private IDisplayMonitors _displays = null!;
    private SessionOrchestrator _sessions = null!;
    private GameMonitor? _games;
    private HotkeyService _hotkeys = null!;

    public EngineStartup(Instance instance, ILoggerFactory loggers, IpcServerOptions serverOptions, EngineHostOptions options)
    {
        _instance = instance;
        _loggers = loggers;
        _serverOptions = serverOptions;
        _options = options;
        _replay = !options.DebugCapture && (options.Rewind ?? !instance.IsTest);
        _ipcLog = loggers.CreateLogger("Ipc");
        _gameLog = loggers.CreateLogger("Games");
        _clipLog = loggers.CreateLogger("Clips");
        _recordingLog = loggers.CreateLogger("Recordings");
        _libraryLog = loggers.CreateLogger("Library");
    }

    private SettingsStore Store => _settings.Store;

    private string RingFolder => Path.Combine(_instance.LocalDataFolder, "ring");

    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.IpcPipeInUse"/>: another engine serves this instance.</exception>
    public EngineHost Start()
    {
        // The pipe first: a second engine must fail before it touches the settings file.
        _server = new IpcServer(Contracts.Ipc.PipeName.ForCurrentUser(_instance.Name), _serverOptions, _ipcLog);
        _audio = _options.AudioHub?.Invoke(_loggers) ?? new AudioDeviceHub(_loggers);
        _cleanup.AddAsync(_server);
        _cleanup.Add(_audio);
        try
        {
            BuildServices();
            return Serve();
        }
        catch
        {
            _cleanup.DisposeAll();
            throw;
        }
    }

    private void BuildServices()
    {
        OpenSettings();
        RecoverRecordings();
        LoadLibraryIndex();
        BuildDeviceList();
        BuildHub(BuildEstimates());
        BuildLibrary();
        BuildFeedback();
        BuildReporters();
        BuildSupervision();
        BuildMeters();
        RemoveStaleRings();
        BuildSessions();
        BuildGames();

        // Only the installed instance falls back to the catalog's default Hotkeys.
        var installed = _instance.Name == Instance.InstalledName;
        _hotkeys = _cleanup.Add(new HotkeyService(Store, _hub, _sessions, installed, _loggers.CreateLogger("Hotkeys")));
    }

    /// <summary>Raises the start-up warnings, registers the commands, then opens the pipe and starts the background work.</summary>
    private EngineHost Serve()
    {
        var heartbeat = new HeartbeatPublisher(_hub, _options.Time);
        var probe = new EncoderProbe(_encoders, _hub);
        RegisterCommands(heartbeat, RaiseStartupWarnings());
        _feedbackCheck.Check();
        var monitorsPublished = _cleanup.Add(new MonitorListPublisher(_displays, _hub).Subscribe());
        _server.Start(_hub, _router);
        _games?.Start();
        if (_options.ProbeEncoders)
        {
            probe.Start();
        }

        if (_options.Heartbeat is { } interval)
        {
            heartbeat.Start(interval);
        }

        return new EngineHost(Services(monitorsPublished), heartbeat, probe, _shutdown.Task);
    }

    private void OpenSettings()
    {
        _settings = SettingsStore.Open(_instance.RoamingDataFolder);
        Store.FolderDefaults = (RingFolder, VideosFolder());
    }

    // The Recordings the last run left open are trimmed before the Library sees them.
    private void RecoverRecordings()
    {
        _runtime = new RuntimeStateStore(_instance.LocalDataFolder, _loggers.CreateLogger("Runtime"));
        _interrupted = InterruptedRecordings.Recover(_runtime.ReadInterrupted(), _recordingLog);
    }

    // The Library at start seeds the hub's initial state, as the device list does.
    private void LoadLibraryIndex()
    {
        _libraryFile = Path.Combine(_instance.RoamingDataFolder, "library.json");
        _libraryLoad = LibraryActor.Load(
            OutputFolder(),
            _libraryFile,
            ProfileForFolder,
            e => _libraryLog.LogWarning("The Library couldn't be read in full: {Message}", e.Message));
    }

    // The device list seeds the hub's initial state; a change is published once the hub exists.
    private void BuildDeviceList()
    {
        _devices = _cleanup.Add(new DeviceListPublisher(
            _options.AudioEndpoints ?? _audio,
            () => Store.Current,
            list => Volatile.Read(ref _hubOnceBuilt)?.Publish(AudioMessages.Changed, list),
            _loggers.CreateLogger("Audio")));

        // A first run records the default devices; before the snapshot, so it starts seeded.
        if (_options.SeedTrackLayout ?? !_instance.IsTest)
        {
            TrackLayoutSeed.Apply(Store, _devices.Current.Devices, _loggers.CreateLogger("Settings"));
        }
    }

    // The storage estimate seeds the initial state likewise.
    private StorageEstimate BuildEstimates()
    {
        _estimates = _cleanup.Add(new EstimatePublisher(
            () => Store.Current,
            new SystemMemoryStatus(),
            EstimatePublisher.FreeDiskBytes,
            RingFolder,
            estimate => Volatile.Read(ref _hubOnceBuilt)?.Publish(CaptureMessages.EstimateChanged, estimate),
            _options.Time));
        return _estimates.Seed();
    }

    private void BuildHub(StorageEstimate estimate)
    {
        var libraryFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutputFolder()));
        _hub = _devices.Seed(list => _hubOnceBuilt = new EventHub(
            Store.Current,
            list,
            _libraryLoad.Items,
            _libraryLoad.Corrupt,
            libraryFolder,
            estimate));

        // These handlers run in this order on every commit, so the order of these lines matters.
        Store.Changed += document => _hub.Publish(SettingsMessages.Changed, new SettingsChanged(document.Revision, document));
        Store.Changed += _devices.OnSettingsChanged;
        Store.Changed += _estimates.OnSettingsChanged;

        // Every commit re-resolves the game session's settings, once the orchestrator exists.
        Store.Changed += _ => Volatile.Read(ref _sessionsOnceBuilt)?.Post(new SettingsUpdated());
        _warnings = new Warnings(_hub, _options.Time);
    }

    private void BuildLibrary()
    {
        var ffmpegLog = _loggers.CreateLogger("FFmpeg");
        var unreachable = new ReplacedWarning(_warnings, ErrorCodes.CleanupLimitUnreachable, "library");
        _library = _cleanup.Add(new LibraryActor(
            _libraryLoad.Items,
            OutputFolder,
            _libraryFile,
            ProfileForFolder,
            _libraryLoad.Corrupt,
            item => _hub.Publish(LibraryMessages.ItemAdded, item),
            item => _hub.Publish(LibraryMessages.ItemChanged, item),
            removed => _hub.Publish(LibraryMessages.ItemRemoved, removed),
            reset => _hub.Publish(LibraryMessages.Reset, reset),
            blocked => _hub.Publish(LibraryMessages.CleanupBlockedChanged, blocked),
            _options.Time,
            e => _libraryLog.LogWarning("A Library disk operation failed: {Message}", e.Message),
            createMissingFolder: !_instance.IsTest,
            readLength: path => MediaLength.Read(path, ffmpegLog),
            folderChanged: changed => _hub.Publish(LibraryMessages.FolderChanged, changed),
            recycle: _options.Recycle ?? RecycleBin.Recycle,
            cleanup: () => SettingsResolver.Resolve(Store.Current, null).Cleanup,
            limitUnreachable: unreachable.Raise,
            log: message => _libraryLog.LogInformation("{Message}", message),
            finalPath: GamePaths.Canonicalize));

        // Runs on the updating thread, so it only posts; the actor ignores a change that keeps the folder.
        Store.Changed += _ => _library.OutputFolderChanged();
        Store.Changed += _ => _library.CleanupChanged();
    }

    private void BuildFeedback()
    {
        var log = _loggers.CreateLogger("Feedback");
        var output = _options.FeedbackSounds ?? (_instance.IsTest ? SilentFeedbackSoundOutput.Instance : new FeedbackSoundOutput());
        _sounds = _cleanup.Add(new FeedbackSoundPlayer(
            output,
            () => Store.Current,
            (message, e) => log.LogWarning(e, "{Message}", message)));
        _feedback = _cleanup.Add(new FeedbackRouter(_warnings, _options.Time, _sounds));
        _feedbackCheck = _cleanup.Add(new FeedbackDeviceCheck(
            _sounds,
            Store,
            _options.AudioEndpoints ?? _audio,
            () => _hub.State.TargetGame?.ProfileId,
            _warnings,
            log));
    }

    private void BuildReporters()
    {
        _dropouts = _cleanup.Add(new DeviceDropoutReporter(_audio, _warnings));

        // One margin for the engine: every session and ring generation shares it, so neither re-arms the warning.
        _margin = new MemoryMargin(new SystemMemoryStatus());
        _marginReporter = _cleanup.Add(new MemoryMarginReporter(_margin, _warnings));
    }

    private void BuildSupervision()
    {
        _router = new CommandRouter(_ipcLog);
        var launcher = _options.AppLauncher ?? AppLauncher.Beside(_instance, _instance.IsTest && _replay ? ["--rewind"] : null);
        _apps = _cleanup.Add(new AppSupervisor(_hub, _warnings, launcher, _options.Time, _loggers.CreateLogger("Supervision")));
    }

    private void BuildMeters()
    {
        _encoders = new VideoEncoderCatalog(_loggers);
        _meters = _cleanup.Add(new TrackMeters(
            _options.MeterAudio ?? _audio,
            levels => _hub.Publish(AudioMessages.Levels, levels),
            _loggers.CreateLogger("Audio")));
    }

    // Ring files a killed engine left (DeleteOnClose covers a crash, not a power loss), before any session makes one.
    private void RemoveStaleRings()
    {
        var stale = RingFile.RemoveStale(RingFolder);
        if (SettingsResolver.Resolve(Store.Current, null).RingPath is { Length: > 0 } ringPath)
        {
            stale += RingFile.RemoveStale(ringPath);
        }

        if (stale > 0)
        {
            _clipLog.LogInformation("Removed {Count} stale Rewind ring files", stale);
        }
    }

    private void BuildSessions()
    {
        var defaultOutputFolder = VideosFolder();
        _displays = _options.Displays ?? new DisplayMonitors(_loggers.CreateLogger<DisplayMonitors>());
        var captureWait = new CaptureWaitWarnings(_warnings, _gameLog);
        _outcomes = new CaptureOutcomes(_hub, _warnings, _feedback, _meters, _feedbackCheck, _loggers);
        _sessions = _cleanup.Add(new SessionOrchestrator(
            new CaptureSessionFactory(CaptureOptions(), _loggers, _audio, _encoders),
            () => Store.Current,
            _outcomes.StateChanged,
            _outcomes.HotkeyPressed,
            _runtime,
            _outcomes.TargetChanged,
            TargetCaptureFor(captureWait),
            _replay ? Clips(defaultOutputFolder) : null,
            _replay ? RecordingsFor(defaultOutputFolder) : null,
            new GpuResetWarnings(_warnings, _clipLog).Recovery,
            _options.Power ?? new SuspendResumeNotifications(_loggers.CreateLogger<SuspendResumeNotifications>()),
            _displays));
        Volatile.Write(ref _sessionsOnceBuilt, _sessions);
    }

    private CaptureSessionOptions CaptureOptions() => new()
    {
        OutputFolder = Path.Combine(_instance.LocalDataFolder, "sessions"),
        Replay = _replay,
        FollowWindow = true,
        RingFolder = RingFolder,
        Margin = _margin,
        ResetGap = _outcomes.ResetGap,
        CaptureFallback = _outcomes.CaptureFallback,
        Warn = _outcomes.CaptureWarning,
    };

    private TargetCapture? TargetCaptureFor(CaptureWaitWarnings captureWait)
    {
        if (_options.DebugCapture)
        {
            return new TargetCapture(
                _ => SessionAudio.None,
                (target, e) => _gameLog.LogWarning(e, "The debug capture of {Name} (process {Pid}) failed", target.Name, target.ProcessId),
                captureWait.Wait);
        }

        return _replay
            ? new TargetCapture(
                snapshot => SessionAudio.FromLayout(snapshot.TrackLayout),
                (target, e) => _gameLog.LogWarning(e, "Rewind of {Name} (process {Pid}) failed", target.Name, target.ProcessId),
                captureWait.Wait)
            : null;
    }

    private ReplayClips Clips(string defaultOutputFolder) => new(
        OutputPathAllocator.System,
        () => defaultOutputFolder,
        _outcomes.ClipSaved,
        _outcomes.SaveFailed,
        _outcomes.ClipNotSaved,
        Saving: _outcomes.Saving,
        Ignored: _outcomes.SaveIgnored,
        Library: _library);

    private Recordings RecordingsFor(string defaultOutputFolder) => new(
        OutputPathAllocator.System,
        () => defaultOutputFolder,
        _outcomes.RecordingStarted,
        _outcomes.RecordingStopped,
        _outcomes.RecordingFailed,
        _library,
        _outcomes.RecordingInfo,
        _runtime,
        Restarted: _outcomes.RecordingRestarted,
        DiskFull: _outcomes.DiskFull,
        RingFits: snapshot => ReplayRings.Fits(snapshot, RingFolder, EstimatePublisher.FreeDiskBytes),
        ReplayDiskFull: _outcomes.ReplayDiskFull,
        MonitorStarted: _outcomes.MonitorRecordingStarted,
        MonitorStopped: _outcomes.MonitorRecordingStopped,
        Warn: _outcomes.RecordingWarning);

    // Test instances detect games only when a test asks: other tests must never see a stray target.changed.
    private void BuildGames()
    {
        if (_options.GameWindows is null && !_options.DebugCapture && !_replay && _instance.IsTest)
        {
            return;
        }

        _games = _cleanup.Add(GameDetectionSetup.Build(_options, Store, _hub, _warnings, _sessions, _gameLog));
    }

    /// <summary>Raises what the last run left to report; returns the <c>library-corrupt</c> warning's id, if raised.</summary>
    private string? RaiseStartupWarnings()
    {
        if (_options.Restarted is { } restarted)
        {
            EngineRestartedWarning.Raise(_hub, _warnings, restarted);
        }

        if (_settings.LoadCode == ErrorCodes.SettingsUnreadable)
        {
            _warnings.Raise(
                ErrorCodes.SettingsUnreadable,
                WarningSeverity.Error,
                "settings",
                "The settings file couldn't be read. It was moved aside and the defaults are in use.",
                overlay: false);
        }

        // An interrupted Recording is never resumed, only reported.
        foreach (var message in _interrupted)
        {
            _warnings.Raise(ErrorCodes.RecordingInterrupted, WarningSeverity.Warning, "recordings", message, overlay: true);
        }

        // Only once they are reported: a start that fails before here reports them next time.
        _runtime.ClearInterrupted();
        if (!_libraryLoad.Corrupt)
        {
            return null;
        }

        return _warnings.Raise(
            ErrorCodes.LibraryCorrupt,
            WarningSeverity.Warning,
            "library",
            "The Library index couldn't be read, so it was rebuilt from the Clips folder; Favourites may be lost. "
                + "Cleanup stays off until you acknowledge this.",
            overlay: false).Id;
    }

    private void RegisterCommands(HeartbeatPublisher heartbeat, string? corruptWarning)
    {
        CommandHandlers.RegisterSettingsAndAudio(_router, Store, _devices, _meters, _server);
        CommandHandlers.RegisterFeedback(_router, _warnings, _library, corruptWarning, _sounds);
        CommandHandlers.RegisterLibrary(_router, _library);
        CommandHandlers.RegisterGamesAndMonitors(_router, Store, _displays);
        CommandHandlers.RegisterDevelopment(_router, Simulator(heartbeat));
        CommandHandlers.RegisterEngine(_router, _apps, _shutdown);
        if (_instance.IsTest)
        {
            CommandHandlers.RegisterTestOnly(_router, _server, heartbeat);
        }
    }

    private DevSimulator Simulator(HeartbeatPublisher heartbeat) => new(
        _instance,
        running => _sessions.Post(new RecordingRunning(running)),
        _feedback.RecordingStarted,
        _feedback.RecordingStopped,
        _feedback.DiskFull,
        _outcomes.ClipNotSaved,
        () => _feedback.ClipSaved(new ClipSaved("dev.simulate")),
        () =>
        {
            // A real hang: no heartbeat, and every publish, snapshot and attach blocks on the hub.
            heartbeat.Stop();
            _hub.Freeze();
        },
        D3D11Device.SimulateReset);

    private EngineServices Services(IDisposable monitorsPublished) => new()
    {
        Server = _server,
        Settings = Store,
        Hub = _hub,
        Router = _router,
        Warnings = _warnings,
        Apps = _apps,
        MonitorsPublished = monitorsPublished,
        Hotkeys = _hotkeys,
        Games = _games,
        Sessions = _sessions,
        Library = _library,
        Feedback = _feedback,
        FeedbackCheck = _feedbackCheck,
        Sounds = _sounds,
        Meters = _meters,
        Devices = _devices,
        Dropouts = _dropouts,
        MarginReporter = _marginReporter,
        Estimates = _estimates,
        Audio = _audio,
    };

    private string VideosFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), _instance.Name);

    private string OutputFolder() => SettingsResolver.Resolve(Store.Current, null).OutputFolder ?? VideosFolder();

    // A top-level folder's Game Profile, for files found on disk.
    private string? ProfileForFolder(string folder) =>
        Store.Current.Profiles
            .FirstOrDefault(profile => string.Equals(profile.Value.Folder, folder, StringComparison.OrdinalIgnoreCase))
            .Key;
}
