using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Audio;
using ReplayBuffer.Core.Monitors;
using ReplayBuffer.Core.Output;
using ReplayBuffer.Core.Replay;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Audio;
using ReplayBuffer.Media;
using ReplayBuffer.Platform.Audio;
using ReplayBuffer.Platform.Capture;
using ReplayBuffer.Platform.FFmpeg;
using ReplayBuffer.Platform.Windows;
using SessionFlow = ReplayBuffer.Contracts.Settings.AudioFlow;
using VideoCodec = ReplayBuffer.Contracts.Settings.VideoCodec;

namespace ReplayBuffer.Engine.Sessions;

/// <summary>
/// The engine's own session options: where the file goes (or that there is none, only a Replay
/// Buffer), and <c>debug-capture</c>'s measurement and encoder experiments. None of it is part of
/// <see cref="SessionSpec"/>.
/// </summary>
internal sealed record CaptureSessionOptions
{
    public static readonly TimeSpan DefaultFirstFrameTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The Replay Buffer (R1, AD-8): the encoders write into a ring sized from the snapshot instead
    /// of a file, in memory or on disk as its <c>replayStorage</c> says, and each
    /// <see cref="CaptureSession.Save"/> writes a Clip from it. No path is allocated, and the outcome
    /// has none.
    /// </summary>
    public bool Replay { get; init; }

    /// <summary>Where a Disk-mode ring's file goes when the snapshot's <c>ringPath</c> is not set; null for none.</summary>
    public string? RingFolder { get; init; }

    /// <summary>The engine's RAM margin (R3), shared by every ring of every session; null for none.</summary>
    public MemoryMargin? Margin { get; init; }

    /// <summary>The folder a file is allocated in when neither <see cref="OutputPath"/> nor the snapshot's Output Folder is set.</summary>
    public required string OutputFolder { get; init; }

    /// <summary>The exact file to write, or null to allocate one per session.</summary>
    public string? OutputPath { get; init; }

    /// <summary>When set, the session records into it and writes the report to <see cref="StatsPath"/>.</summary>
    public CaptureStatsCollector? Stats { get; init; }

    public string? StatsPath { get; init; }

    /// <summary>The run length the stats report records.</summary>
    public int RequestedSeconds { get; init; }

    public int? GpuPriority { get; init; }

    public CaptureStages Stages { get; init; } = CaptureStages.Full;

    public string NvencPreset { get; init; } = DebugCaptureCommand.DefaultNvencPreset;

    public string? NvencTune { get; init; }

    public ColorPath Color { get; init; } = ColorPath.X1;

    /// <summary>
    /// An FFmpeg encoder name that every session uses whatever the snapshot's codec, bypassing the
    /// probe (<c>debug-capture --encoder</c>, for tests); null for the probe's encoder for the snapshot's codec.
    /// </summary>
    public string? Encoder { get; init; }

    /// <summary>Called once, on the capture thread, with the anchor QPC once the first frame has opened the output.</summary>
    public Action<long>? Anchored { get; init; }

    /// <summary>
    /// For tests: called on the writer thread with a Clip's final path just before it is written; a
    /// non-null result is thrown as that write's failure.
    /// </summary>
    public Func<string, Exception?>? ClipWriteFault { get; init; }

    /// <summary>
    /// For tests: called on a forced restart's thread (<see cref="CaptureSession.RestartChain"/>)
    /// just before the new chain is built; a non-null result is thrown there, as a failed build.
    /// </summary>
    public Func<Exception?>? RestartFault { get; init; }

    /// <summary>
    /// For tests: called at the start of each GPU-reset recovery attempt
    /// (<see cref="CaptureSession.RecoverDevice"/>), once the lost device and chains are torn down; a
    /// non-null result is thrown there, as a failed attempt.
    /// </summary>
    public Func<Exception?>? RecoveryFault { get; init; }

    /// <summary>
    /// For tests: called on the capture thread for each frame after the anchor; while it returns
    /// true the frame is dropped, as on the secure desktop (lock, UAC, Ctrl+Alt+Del, T-04), where
    /// WGC delivers none and the pacer repeats the last frame.
    /// </summary>
    public Func<bool>? StarveFrames { get; init; }

    /// <summary>How often a session holding a Replay Buffer, or the Monitor session, checks from its anchor on whether its GPU device was removed or reset.</summary>
    public TimeSpan DeviceWatchInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// For tests: called before any WGC capture is created; a non-null result is thrown there, as
    /// WGC failing. A Monitor session then falls back to Desktop Duplication (6.10); a game session fails.
    /// </summary>
    public Func<Exception?>? WgcFault { get; init; }

    /// <summary>
    /// Called once, on the opening thread, with the message when a Monitor session's WGC capture
    /// failed and it captures through Desktop Duplication instead (<see cref="ErrorCodes.MonitorCaptureFallback"/>);
    /// null ignores it.
    /// </summary>
    public Action<string>? CaptureFallback { get; init; }

    /// <summary>
    /// When set, a game session whose window closes keeps running with no capture, the pacer
    /// repeating its last frame, and posts <c>Closed</c>: the orchestrator retargets it to the
    /// process's next window (keeping its Replay Buffer) or ends it. When not set, it stops.
    /// </summary>
    public bool FollowWindow { get; init; }

    /// <summary>With no first frame by then, the session posts a <see cref="ErrorCodes.CaptureFailed"/> failure.</summary>
    public TimeSpan FirstFrameTimeout { get; init; } = DefaultFirstFrameTimeout;

    /// <summary>
    /// Called on a background thread with the message when a switch to a profile's new encoder
    /// chain left a gap of more than one frame before the next Clip (<see cref="ErrorCodes.ResetGap"/>);
    /// null ignores it.
    /// </summary>
    public Action<string>? ResetGap { get; init; }

    /// <summary>
    /// Dev only (<c>debug-capture --monitors</c>, spike 11.1; kept for the NFR-1 cost measurements of
    /// <c>measure-path-a.ps1 -mon&lt;mode&gt;</c>): a file session captures these monitors (GDI device
    /// names; empty for every attached monitor) composited into one canvas (<see cref="CompositeCapture"/>)
    /// instead of a window. A Monitor session composites its resolved screens instead (story 11.3).
    /// </summary>
    public IReadOnlyList<string>? CompositeMonitors { get; init; }

    /// <summary>Spike 11.1, dev only: <see cref="CompositeMonitors"/> captures every monitor through Desktop Duplication.</summary>
    public bool CompositeDuplication { get; init; }

    /// <summary>For tests (story 12.4): how long a size move's built chain waits before it arms, so a test can press Record while it is pending. Zero in production.</summary>
    public TimeSpan MoveArmDelay { get; init; }

    /// <summary>For tests (story 11.3): the composited screens, by GDI device name, captured through Desktop Duplication instead of WGC.</summary>
    public IReadOnlyCollection<string>? DuplicationScreens { get; init; }

    /// <summary>
    /// Called once, on the opening thread, with a warning code and message for the Monitor session
    /// (story 11.3): <see cref="ErrorCodes.MonitorRecordingScaled"/> when a composited canvas over 4096 px
    /// has to stay H.264; null ignores it.
    /// </summary>
    public Action<string, string>? Warn { get; init; }
}

/// <summary>
/// Starts <see cref="CaptureSession"/>s with one set of options, the engine's audio hub and one
/// encoder catalog; with no catalog given, the factory probes on its own, once per codec.
/// </summary>
internal sealed class CaptureSessionFactory(CaptureSessionOptions options, ILoggerFactory loggers, IAudioDeviceHub audio, IVideoEncoderCatalog? encoders = null)
    : ICaptureSessionFactory
{
    private readonly IVideoEncoderCatalog _encoders = encoders ?? new VideoEncoderCatalog(loggers);

    public ICaptureSession Start(SessionSpec spec, ISessionEvents events)
    {
        FFmpegLibrary.Initialize(loggers.CreateLogger("FFmpeg"));
        var session = new CaptureSession(spec, events, options, audio, _encoders, loggers);
        try
        {
            session.Open();
        }
        catch
        {
            session.Abandon();
            throw;
        }

        return session;
    }
}

/// <summary>
/// One capture session: WGC window → scaler → paced video encoder, and the session's Tracks, each
/// an AAC stream summed from its devices' hub streams (AD-20), all into one fragmented MP4. Each
/// device is subscribed once and cut at the anchor by one <see cref="AudioTimeline"/>, which feeds
/// every Track holding it (<see cref="AudioFanOut"/>); each Track's <see cref="TrackMixer"/> sums its
/// devices by frame position, through a limiter that acts only above full scale. A Track with no
/// devices is silence, advanced from QPC by a timer, off the capture thread; at the end every Track
/// is padded with silence to one common end frame, the longest device's or the last video frame's,
/// whichever is later. The first frame is the anchor, every stream's PTS 0. The snapshot's
/// <c>video</c> sets the encoder (the probe's for its codec, else H.264), the output size, the fps,
/// the bitrate and the rate control; with <c>matchSource</c> on, a game's output size is instead the
/// game's own at the first frame, and a size that holds 3 s moves the Replay Buffer to a new chain
/// (AD-26, stories 12.1 and 12.2). The capture thread only scales each frame into the chain's output,
/// letterboxed (<see cref="FrameScaler"/>), and posts it; the encoder thread emits exactly one frame
/// per slot of the fps, repeating the last one when nothing new arrived (story 1.11,
/// <see cref="PacedVideoEncoder"/>). A retarget swaps only the WGC capture, so the file keeps one
/// unbroken set of streams. Mic mute (AD-13) is one <see cref="MicMuteSchedule"/>, set from the spec
/// before anything opens and then by <see cref="SetMicMuted"/>; each Mic Device's input to each
/// <c>muteAffected</c> Track passes through a <see cref="MicMuteGate"/>, so it is silenced there,
/// before the mix, from the press's frame, and stays audible in every other Track. All of that per
/// spec is one <see cref="EncoderChain"/>. In Replay mode (<see cref="CaptureSessionOptions.Replay"/>)
/// the streams go into the chain's <see cref="ReplayRingSink"/> instead of a file, and
/// <see cref="Save"/> writes a Clip from it on a thread of its own. The ring is sized from the
/// snapshot's Buffer Window at the anchor; a new window from <see cref="SetBufferWindow"/> resizes
/// it at the next Reset (AD-8), into a new ring generation seeded from the Reset keyframe, so Clips
/// stay gapless. The encoders write into the ring through a <see cref="RecordingTap"/>, and
/// <see cref="Record"/> attaches a <see cref="RecordingWriter"/> to it: a Recording from the same
/// encoder chain. A profile's new Tracks or video (<see cref="PrepareChain"/>, R7, R8) get a chain
/// of their own, built in advance on a background thread and idle; the next save press whose write
/// starts arms it: its audio starts at the press, and the first frame captured after the press
/// makes it the current chain, which later saves cut from. The old chain is fed until every save
/// bound to it has cut, then torn down off the capture and orchestrator threads, so at most two
/// chains live at once (AD-7). A new chain whose first keyframe comes more than a frame after the
/// old Clip's end raises <see cref="CaptureSessionOptions.ResetGap"/>. A forced restart
/// (<see cref="RestartChain"/>, R4) tears the current chain down on a thread of its own and rebuilds
/// the same spec into a new generation of the same ring, dropping the frames captured meanwhile;
/// the Recording on the old chain ends at its last packet once the orchestrator has been told
/// (<see cref="ISessionEvents.ChainRestarted"/>), and a failed rebuild fails the session. A switch
/// at a Reset (<see cref="SwitchChain"/>, while a Monitor Recording holds the third chain) reuses
/// that machinery with the new spec, into a fresh ring, and raises the gap as
/// <see cref="CaptureSessionOptions.ResetGap"/>. A <see cref="SessionKind.Monitor"/> session (6.9)
/// captures the monitor its snapshot's <c>monitorRecordingMonitor</c> names (else the primary one,
/// <see cref="MonitorFinder"/>) on a device of that monitor's adapter, holds no Replay Buffer and
/// writes no file of its own: its chain feeds only the Recording <see cref="Record"/> attaches. Its
/// anchor is posted (<see cref="ISessionEvents.Anchored"/>) so the orchestrator can start it. When its
/// WGC monitor item can't be created or started, it captures the same monitor through DXGI Desktop
/// Duplication instead (AD-12, 6.10), into the same chain, and raises
/// <see cref="CaptureSessionOptions.CaptureFallback"/> once; a game session never falls back.
/// A GPU driver reset (R3): a failure carrying <see cref="D3D11Device.DeviceRemoved"/> or
/// <see cref="D3D11Device.DeviceReset"/>, or a device that reports itself lost (checked every
/// <see cref="CaptureSessionOptions.DeviceWatchInterval"/> from the anchor on), stops feeding the
/// chains and posts <see cref="ISessionEvents.DeviceLost"/> once, swallowing later failures. Nothing
/// is built, armed, switched, resized or recorded while the device is lost, and a save cuts the ring
/// as it is, with no keyframe asked for. Each <see cref="RecoverDevice"/> is one attempt, on a thread
/// of its own: the first tears WGC, every chain (keeping the ring) and the device down; then a new
/// device on the Target's adapter, the chain rebuilt into a new generation of the same ring, and new
/// capture; it posts <see cref="ISessionEvents.ChainRestarted"/>, then ends the old chain's Recording,
/// or <see cref="ISessionEvents.DeviceLost"/> again when it failed. Only sessions with a Replay Buffer,
/// and the Monitor session, recover; a file-mode session fails as before. A sleep
/// (<see cref="Suspend"/>, T-02) takes the same lost state without a loss: the same teardown, the
/// Recording ended at its last fragment, and the resume's <see cref="RecoverDevice"/> rebuilds.
/// </summary>
internal sealed class CaptureSession : ICaptureSession
{
    /// <summary>One AAC frame (1024 samples at 48 kHz) in µs, rounded up.</summary>
    private const long AacFrameUs = ((1024 * 1_000_000L) + AudioTimeline.SampleRate - 1) / AudioTimeline.SampleRate;

    /// <summary>How long a save waits for every stream to pass the press before it cuts.</summary>
    private static readonly TimeSpan SaveWait = TimeSpan.FromSeconds(1);

    /// <summary>How long <see cref="End"/> waits for a running GPU device recovery attempt.</summary>
    private static readonly TimeSpan RecoveryJoinWait = TimeSpan.FromSeconds(10);

    private SessionSpec _spec;
    private readonly ISessionEvents _events;
    private readonly CaptureSessionOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly Lock _gate = new();
    private readonly IAudioDeviceHub _audio;
    private readonly MicMuteSchedule _micMute;
    private readonly CaptureStatsCollector? _stats;
    private readonly Timer _firstFrameTimer;

    private WindowInfo? _window;
    private MonitorInfo? _monitor;
    private IReadOnlyList<MonitorPlacement>? _composite;

    // Story 12.1: the game's captured size at the anchor, for chains under matchSource; written before the anchor is published.
    // Story 12.2: then the size the Replay Buffer last moved to.
    private VideoSize? _sourceSize;

    // Story 12.2 (AD-26): the held size change. The watch runs on the capture thread, under the gate;
    // a move's state is under the chain lock: the size it moves to, whether its build-ahead chain arms
    // without a save, and whether that arm is already queued.
    private readonly SourceSizeWatch _sizeWatch = new(Stopwatch.Frequency);
    private VideoSize? _movingTo;
    private bool _armMove;
    private bool _armPending;
    private volatile bool _buildAhead = true;

    // Story 12.2 (QA): a Record press reserves the current chain until its writer attaches (at most
    // RecordReserve), so no size move starts or arms in between. QPC; under the chain lock.
    private long _recordReservedUntil;
    private static readonly TimeSpan RecordReserve = TimeSpan.FromSeconds(10);
    private string? _path;
    private D3D11Device? _device;
    private ICaptureSource? _capture;
    private volatile bool _outputFinished;

    // R1's exit drain, orchestrator thread only: the teardown keeps the ring for saves until Release.
    private bool _keepRing;
    private bool _released;

    // The Replay Buffer's last Reset in µs since the anchor (R5): the end of the last Clip the
    // orchestrator Reset at. It only moves forward.
    private long _resetUs = long.MinValue;

    // How far this session's Clips have claimed the ring (R6): a write cuts from no earlier than the
    // end of the newest write before it, so two concurrent Clips never overlap.
    private readonly ClipClaim _claim = new();

    // The window SetBufferWindow asked for, applied at the next Reset; 0 for none.
    private volatile int _pendingWindow;

    // The encoder chains (4.9). _chainLock guards them all, and is only ever taken after _gate,
    // never before it. The chain the capture thread draws into and saves cut from, the one armed
    // at a press (with the press) and the replaced one still fed are written under both locks, so
    // the capture thread reads them under _gate. The rest is under _chainLock only: the chain built
    // and idle, the spec asked for, a build running, the background threads, and whether End has
    // closed them.
    private readonly Lock _chainLock = new();
    private readonly List<Thread> _workers = [];
    private EncoderChain? _current;
    private EncoderChain? _armed;
    private long _armedQpc;
    private EncoderChain? _retiring;
    private EncoderChain? _prepared;
    private SessionSpec? _wanted;
    private bool _building;

    // Chains being torn down after a newer spec replaced them: a build waits for them (AD-7's two chains).
    private int _tearingDown;
    private volatile bool _chainsClosed;

    // A forced restart (R4), under the chain lock: whether one runs, and its thread, which End joins.
    private bool _restarting;
    private Thread? _restartThread;

    // A GPU device loss (R3), under the chain lock; _deviceLost is written under the gate too, so the
    // capture thread reads it there. Whether the device is lost, whether a recovery attempt runs,
    // whether the lost device and chains are torn down yet, the lost chain's Recording tap (open
    // until the recovery is posted), the spec rebuilt, and the attempt's thread, which End joins.
    private bool _deviceLost;
    private bool _recovering;

    // A sleep (T-02), under the chain lock: from Suspend until the resume's RecoverDevice. An attempt
    // still running then installs nothing, so no capture or chain built before the sleep runs across it.
    private bool _suspended;
    private bool _tornDown;
    private RecordingTap? _lostTap;
    private SessionSpec? _rebuildSpec;
    private Thread? _recoveryThread;

    // Checks the device from the anchor on; under the gate.
    private Timer? _deviceWatch;

    private readonly IVideoEncoderCatalog _encoders;
    private long _anchorQpc;
    private long _lastFrameQpc;
    private bool _anchored;
    private bool _stopped;
    private string _stopReason = "";
    private Exception? _failure;

    public CaptureSession(SessionSpec spec, ISessionEvents events, CaptureSessionOptions options, IAudioDeviceHub audio, IVideoEncoderCatalog encoders, ILoggerFactory loggers)
    {
        _spec = spec;
        _audio = audio;
        _events = events;
        _options = options;
        _encoders = encoders;
        _loggers = loggers;
        _log = loggers.CreateLogger<CaptureSession>();
        _stats = options.Stats;
        _micMute = new MicMuteSchedule(spec.MicMuted);
        _firstFrameTimer = new Timer(_ => OnFirstFrameTimeout(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Opens the device, subscribes to the audio devices and starts capture; the output opens on the first frame.</summary>
    public void Open()
    {
        // Story 11.3: several screens composited; the plan sets the codec and size before the encoder resolves.
        // Story 12.2: so does one screen under matchSource (its physical mode, HEVC or AV1 over 4096 px).
        if (IsMonitor && _options.CompositeMonitors is null)
        {
            if (_spec.Screens is { Count: > 1 } screens)
            {
                PlanCanvas(screens);
            }
            else if (_spec.Snapshot.Video.MatchSource)
            {
                var one = _spec.Screens is [var resolved]
                    ? resolved
                    : MonitorFinder.Find(_spec.Snapshot.MonitorRecordingMonitor, _log) is var found
                        ? new DisplayMonitor(found.DeviceName, found.DeviceName, 0, 0, found.IsPrimary)
                        : null;
                if (one is not null)
                {
                    var placements = MonitorCanvas.Place([one]);
                    if (placements.Count == 1)
                    {
                        (_spec, _) = PlanCanvas(_spec, [one], placements, CanEncode, _options.Warn, _log);
                    }
                }
            }
        }

        // Before anything is opened: the first codec use may run the probe.
        var (encoder, codec) = ResolveEncoder(_spec);
        var target = _spec.Target;
        if (_composite is not null)
        {
            _monitor = _composite[0].Monitor;
            _log.LogInformation("Monitor capture of {Count} screens composited: {Screens}", _composite.Count, string.Join("; ", _composite));
        }
        else if (IsMonitor)
        {
            // One screen, the pass-through: the resolved one (monitorRecordingScreens), else the old
            // monitorRecordingMonitor's (null for the primary), captured as before 11.3.
            _monitor = MonitorFinder.Find(_spec.Screens is [var only] ? only.DeviceName : _spec.Snapshot.MonitorRecordingMonitor, _log);
            _log.LogInformation("Monitor capture of {Device}", _monitor.DeviceName);
        }
        else if (_options.CompositeMonitors is { } names)
        {
            // debug-capture --monitors (spike 11.1, kept for cost measurements): a file session of the
            // composited monitors, its device on the primary composited monitor's adapter.
            _composite = MonitorCanvas.Place(names);
            if (_composite.Count == 0)
            {
                throw new EngineFailure(ErrorCodes.CaptureFailed, $"None of the monitors '{string.Join(", ", names)}' is attached.");
            }
        }
        else
        {
            _window = WindowFinder.FromHandle(target.Window);
        }

        if (!_options.Replay && !IsMonitor)
        {
            _path = _options.OutputPath is { } explicitPath
                ? Path.GetFullPath(explicitPath)
                : OutputNaming.Allocate(_spec.Snapshot.OutputFolder ?? _options.OutputFolder, _window?.Title ?? "Capture", DateTime.Now, File.Exists);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        }

        _device = (_monitor ?? _composite?[0].Monitor) is { } monitor
            ? D3D11Device.CreateForMonitor(monitor.Handle, _loggers.CreateLogger<D3D11Device>())
            : D3D11Device.Create(target.Window, _loggers.CreateLogger<D3D11Device>());
        if (_options.GpuPriority is { } priority)
        {
            SetGpuPriority(priority);
        }

        // The first chain: its taps hold the last second of blocks until the anchor starts its audio.
        var first = NewChain(_spec, encoder, codec, _path, _device);
        lock (_gate)
        {
            lock (_chainLock)
            {
                _current = first;
            }
        }

        first.Subscribe();
        _log.LogInformation("Mic {State} at start; mic mute covers {Inputs} Mic Device input(s) in muteAffected Tracks",
            _spec.MicMuted ? "muted" : "unmuted", _spec.Audio.GatedMicInputs().Count());

        if (IsMonitor)
        {
            // Started as it is chosen: a WGC item that fails to start falls back to Desktop Duplication.
            _firstFrameTimer.Change(_options.FirstFrameTimeout, Timeout.InfiniteTimeSpan);
            StartMonitorCapture(_device, first.Fps, StartInstalled);
            return;
        }

        ICaptureSource capture = _composite is not null ? NewComposite(_device, first.Fps) : NewCapture(target.Window, _device, first.Fps);
        lock (_gate)
        {
            _capture = capture;
        }

        _firstFrameTimer.Change(_options.FirstFrameTimeout, Timeout.InfiniteTimeSpan);
        capture.Start();
    }

    /// <summary>For tests: which API the session captures through, <c>wgc</c> or <c>desktop-duplication</c>.</summary>
    internal string CaptureApi { get; private set; } = "wgc";

    /// <summary>For tests: the Desktop Duplication capture's next acquire behaves as <c>DXGI_ERROR_ACCESS_LOST</c>.</summary>
    internal void SimulateCaptureAccessLost()
    {
        ICaptureSource? capture;
        lock (_gate)
        {
            capture = _capture;
        }

        if (capture is not DesktopDuplicationCapture duplication)
        {
            throw new InvalidOperationException("The session doesn't capture through Desktop Duplication.");
        }

        duplication.SimulateAccessLost();
    }

    /// <summary>
    /// Story 11.3 (AD-25): the canvas of <paramref name="screens"/>, placed by their display modes now.
    /// Its codec and size replace the snapshot's video (HEVC, else AV1, over H.264's 4096 px, at full
    /// size), so the one encoder chain encodes the whole canvas. A canvas over 4096 px that has to stay
    /// H.264 is fitted to 4096 with <see cref="ErrorCodes.MonitorRecordingScaled"/>. Fewer than two
    /// screens still attached: the one left is captured as today; none, the start fails with
    /// <see cref="ErrorCodes.MonitorRecordingNoScreens"/>.
    /// </summary>
    private void PlanCanvas(IReadOnlyList<DisplayMonitor> screens)
    {
        (_spec, _composite) = PlanCanvas(_spec, screens, MonitorCanvas.Place(screens), CanEncode, _options.Warn, _log);
    }

    /// <summary>
    /// <see cref="PlanCanvas(IReadOnlyList{DisplayMonitor})"/> on screens already placed: the spec to
    /// encode with, and the placements to composite (null when one screen is left, captured as today).
    /// </summary>
    internal static (SessionSpec Spec, IReadOnlyList<MonitorPlacement>? Composite) PlanCanvas(
        SessionSpec spec,
        IReadOnlyList<DisplayMonitor> screens,
        IReadOnlyList<MonitorPlacement> placements,
        Func<VideoCodec, bool> canEncode,
        Action<string, string>? warn,
        ILogger log)
    {
        if (placements.Count == 0)
        {
            throw new EngineFailure(ErrorCodes.MonitorRecordingNoScreens, "None of the screens chosen to record is connected.");
        }

        if (placements.Count == 1)
        {
            var only = screens.First(s => string.Equals(s.DeviceName, placements[0].Monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (!spec.Snapshot.Video.MatchSource)
            {
                return (spec with { Screens = [only] }, null);
            }

            // Story 12.2 (AD-26): one screen under matchSource records at its physical mode size,
            // through AD-25's rule: HEVC, else AV1, over H.264's 4096 px. Captured as one screen.
            var single = MonitorCanvasRule.Plan(
                [new DisplayMonitor(only.DeviceName, only.DeviceName, placements[0].Width, placements[0].Height, only.Primary, null, placements[0].X, placements[0].Y, placements[0].RefreshHz)],
                spec.Snapshot.Video.Codec,
                canEncode);
            log.LogInformation("One screen at source size: {Native}x{NativeHeight} encoded {Codec} {Width}x{Height}",
                single.NativeWidth, single.NativeHeight, single.Codec, single.Width, single.Height);
            if (single.Codec == VideoCodec.H264 && single.Scaled)
            {
                warn?.Invoke(
                    ErrorCodes.MonitorRecordingScaled,
                    $"No HEVC or AV1 encoder works on this PC, so the {single.NativeWidth} × {single.NativeHeight} Monitor Recording is scaled down to {single.Width} × {single.Height} for H.264.");
            }

            var oneVideo = spec.Snapshot.Video;
            return (spec with { Screens = [only], Snapshot = spec.Snapshot with { Video = oneVideo with { Codec = single.Codec, Width = single.Width, Height = single.Height } } }, null);
        }

        var placed = placements.Select(p => new DisplayMonitor(p.Monitor.DeviceName, p.Monitor.DeviceName, p.Width, p.Height, p.Monitor.IsPrimary, null, p.X, p.Y, p.RefreshHz)).ToList();
        var video = spec.Snapshot.Video;
        var plan = MonitorCanvasRule.Plan(placed, video.Codec, canEncode);
        log.LogInformation("Canvas {Native}x{NativeHeight} encoded {Codec} {Width}x{Height} (Output codec {Output})",
            plan.NativeWidth, plan.NativeHeight, plan.Codec, plan.Width, plan.Height, video.Codec);
        if (plan.Codec == VideoCodec.H264 && plan.Scaled)
        {
            warn?.Invoke(
                ErrorCodes.MonitorRecordingScaled,
                $"No HEVC or AV1 encoder works on this PC, so the {plan.NativeWidth} × {plan.NativeHeight} Monitor Recording is scaled down to {plan.Width} × {plan.Height} for H.264.");
        }

        return (spec with { Snapshot = spec.Snapshot with { Video = video with { Codec = plan.Codec, Width = plan.Width, Height = plan.Height } } }, placements);
    }

    /// <summary>Whether this PC has a working encoder for <paramref name="codec"/> (the probe's answer); false when the probe fails.</summary>
    private bool CanEncode(VideoCodec codec)
    {
        try
        {
            return _encoders.EncoderFor(codec) is not null;
        }
        catch (Exception e)
        {
            _log.LogWarning("The {Codec} encoder probe failed: {Message}", codec, e.Message);
            return false;
        }
    }

    /// <summary>
    /// Every chain of the session, made here (story 12.4): <paramref name="spec"/>'s on
    /// <paramref name="device"/>, at the snapshot's fixed size, or, for a game under <c>matchSource</c>
    /// once its source size is known, at that size or a move's (<see cref="SizedToSource(EncoderChain)"/>).
    /// <paramref name="path"/> is the file a session with no Replay Buffer writes.
    /// </summary>
    private EncoderChain NewChain(SessionSpec spec, string encoder, VideoCodec codec, string? path, D3D11Device device) =>
        SizedToSource(new EncoderChain(spec, encoder, codec, path, _options, device, _audio, _micMute, _loggers, _log, Fail));

    /// <summary>
    /// Story 12.1: a game session's <paramref name="chain"/>, not yet built, sized from the game's
    /// captured size at the anchor when its snapshot has <c>matchSource</c> on (Core's
    /// <see cref="SourceOutputSize"/>: even sides, scaled only to fit the chain's codec, and a game
    /// under 640×360 keeping the snapshot's size). Unchanged otherwise, and for the Monitor session.
    /// </summary>
    private EncoderChain SizedToSource(EncoderChain chain) => SizedToSource(chain, IsMonitor, _movingTo ?? _sourceSize);

    /// <summary><see cref="SizedToSource(EncoderChain)"/> for a session that <paramref name="isMonitor"/> or not, whose game was <paramref name="source"/> at the anchor.</summary>
    internal static EncoderChain SizedToSource(EncoderChain chain, bool isMonitor, VideoSize? source)
    {
        if (!isMonitor && chain.Spec.Snapshot.Video.MatchSource && source is { } captured
            && SourceOutputSize.For(captured.Width, captured.Height, chain.Codec) is { } size)
        {
            chain.SizeTo(new VideoSize(size.Width, size.Height));
        }

        return chain;
    }

    /// <summary>Story 12.2: whether a size move may build its chain ahead (false while a Monitor Recording holds the other chain).</summary>
    public void SetBuildAhead(bool allowed) => _buildAhead = allowed;

    /// <summary>
    /// Story 12.2 (AD-26): one captured frame's size, under the gate. With <c>matchSource</c> on, no
    /// move running and the current chain live, a size that held 3 s (none while a Recording is
    /// attached) starts a move; a move's prepared chain waiting for a Recording to end arms once it has.
    /// </summary>
    private void WatchSourceSize(long qpc, VideoSize content)
    {
        EncoderChain? current;
        bool moving;
        bool reserved;
        lock (_chainLock)
        {
            current = _current;
            moving = _movingTo is not null;
            reserved = RecordReservedLocked();
            TryStartArmLocked();
        }

        if (moving || current is not { IsBuilt: true, IsDisposed: false, Spec.Snapshot.Video.MatchSource: true })
        {
            return;
        }

        if (_sizeWatch.Observe(qpc, content.Width, content.Height, current.OutputSize.Width, current.OutputSize.Height, current.Codec, reserved || current.Tap is { HasWriter: true }) is { } target)
        {
            StartMove(new VideoSize(target.Width, target.Height));
        }
    }

    /// <summary>
    /// Story 12.2: moves the Replay Buffer to <paramref name="target"/>, under the gate. With build-ahead
    /// allowed, the current spec's chain is built ahead at that size in the prepared slot (the
    /// <c>ChainKey</c> is unchanged, so no settings change would) and armed without a save once built
    /// (<see cref="ArmMove"/>). While a Monitor Recording holds the other chain, the current chain is
    /// torn down first and rebuilt at that size into a fresh ring (<see cref="SwitchChain"/>'s
    /// machinery). Either way a gap over a frame raises <c>reset-gap</c>. Refused, to be found again
    /// after another 3 s, while any switch, build, teardown, restart or recovery runs, a chain is
    /// prepared, or a Recording is attached.
    /// </summary>
    private void StartMove(VideoSize target)
    {
        lock (_chainLock)
        {
            if (_chainsClosed || _keepRing || _outputFinished || _stopped || _deviceLost || _restarting || _building || _tearingDown > 0
                || _armed is not null || _retiring is not null || _prepared is not null || RecordReservedLocked()
                || _current is not { IsBuilt: true, IsDisposed: false, Tap.HasWriter: false } old)
            {
                _log.LogInformation("The game's size changed to {Size} and held; the move waits, since another chain change or a Recording is running", target);
                return;
            }

            _movingTo = target;
            if (_buildAhead)
            {
                _log.LogInformation("The game's size changed to {Size} and held 3 s: the Replay Buffer moves to it (built ahead)", target);
                _wanted = old.Spec;
                _armMove = true;
                StartBuildLocked();
            }
            else
            {
                _log.LogInformation("The game's size changed to {Size} and held 3 s: the Replay Buffer moves to it, torn down first while a Monitor Recording runs", target);
                _wanted = null;
                _restarting = true;
                _restartThread = new Thread(() => Restart(old, "the game's size changed while a Monitor Recording runs", old.Spec)) { Name = "Encoder chain move", IsBackground = true };
                _restartThread.Start();
            }
        }
    }

    /// <summary>Story 12.2: queues <see cref="ArmMove"/> when a move's chain is prepared and nothing blocks it. Under the chain lock.</summary>
    private void TryStartArmLocked()
    {
        if (!_armMove || _armPending || _prepared is null || _armed is not null || _retiring is not null || _restarting || _deviceLost
            || _current is not { Tap.HasWriter: false } || _chainsClosed || RecordReservedLocked())
        {
            return;
        }

        _armPending = true;
        StartWorkerLocked("Encoder chain move", ArmMove);
    }

    /// <summary>
    /// Story 12.2: <see cref="TryArm"/>'s arm for a size move, with no save: on a background thread,
    /// the prepared chain is armed now, its audio starting here; the first frame captured after it makes
    /// it current, and the old chain, bound to no save, retires at once, with <c>reset-gap</c> when its
    /// last frame and the new chain's first keyframe are more than a frame apart. Never while a
    /// Recording is attached: it then waits for the next frame after the Recording ends.
    /// </summary>
    private void ArmMove()
    {
        if (_options.MoveArmDelay > TimeSpan.Zero)
        {
            Thread.Sleep(_options.MoveArmDelay);
        }

        EncoderChain? armed = null;
        long armQpc;
        lock (_gate)
        {
            armQpc = Stopwatch.GetTimestamp();
            lock (_chainLock)
            {
                _armPending = false;
                if (!_stopped && _armMove && _prepared is { } prepared && _armed is null && _retiring is null && !_restarting && !_deviceLost
                    && _current is { Tap.HasWriter: false } current && !_outputFinished && !_keepRing && !_chainsClosed && !RecordReservedLocked())
                {
                    _prepared = null;
                    _wanted = null;
                    _armMove = false;
                    _armed = prepared;
                    _armedQpc = armQpc;
                    current.SwitchEndUs = MicrosAt(armQpc);
                    armed = prepared;
                }
            }
        }

        if (armed is null)
        {
            return;
        }

        armed.StartAudio(armQpc);
        _log.LogInformation("The size move switches the Replay Buffer to its new encoder chain {At:0.000} s after the anchor", MicrosAt(armQpc) / 1e6);
    }

    /// <summary>Story 12.2: a move ends once a chain at its size is current: that size is the source's from here. Under the chain lock.</summary>
    private void CompleteMoveLocked(EncoderChain current)
    {
        if (_movingTo is { } moved && SourceOutputSize.For(moved.Width, moved.Height, current.Codec) is { } size
            && size == (current.OutputSize.Width, current.OutputSize.Height))
        {
            _sourceSize = moved;
            _movingTo = null;
            _armMove = false;
        }
    }

    /// <summary>For tests (12.4): whether a size move is pending (building or waiting to arm), so a Record press now cancels it.</summary>
    internal bool MovePending
    {
        get
        {
            lock (_chainLock)
            {
                return _armMove;
            }
        }
    }

    /// <summary>For tests (12.4): how many size moves a Recording's start cancelled. Under the chain lock.</summary>
    internal int MovesCancelled { get; private set; }

    /// <summary>Story 12.2: whether a Record press holds the current chain until its writer attaches. Under the chain lock.</summary>
    private bool RecordReservedLocked() => Stopwatch.GetTimestamp() < _recordReservedUntil;

    /// <summary>
    /// Story 12.2 (QA): a Recording starts on the current chain, so a pending size move is cancelled:
    /// its prepared chain is torn down, a build in flight is torn down when it ends (its spec is no
    /// longer wanted), and an arm queued finds nothing to arm. The watch finds the size again after the
    /// Recording, if it still holds. A profile's chain waiting for a save is left alone. Under the chain lock.
    /// </summary>
    private void CancelMoveLocked()
    {
        if (!_armMove)
        {
            return;
        }

        _log.LogInformation("A Recording starts: the size move to {Size} is cancelled, and found again after it if the size still holds", _movingTo);
        MovesCancelled++;
        DropMoveLocked();
        _wanted = null;
        if (_prepared is { } prepared)
        {
            _prepared = null;
            TearDownLocked(prepared);
        }
    }

    /// <summary>Story 12.2: forgets a pending move (a failed build, a lost device, a dropped prepared chain); the size must hold again. Under the chain lock.</summary>
    private void DropMoveLocked()
    {
        _movingTo = null;
        _armMove = false;
    }

    /// <summary>Whether this is the Monitor session (6.9), not a game window's.</summary>
    private bool IsMonitor => _spec.Kind == SessionKind.Monitor;

    /// <inheritdoc/>
    public string? MonitorDevice => _monitor is { DeviceName.Length: > 0 } monitor ? monitor.DeviceName : null;

    /// <summary>Story 11.3: the screens still composited, else <see cref="MonitorDevice"/> alone.</summary>
    public IReadOnlyList<string> MonitorDevices
    {
        get
        {
            ICaptureSource? capture;
            lock (_gate)
            {
                capture = _capture;
            }

            return capture is CompositeCapture composite ? composite.LiveDevices
                : _composite is not null ? [.. _composite.Select(p => p.Monitor.DeviceName)]
                : MonitorDevice is { } device ? [device] : [];
        }
    }

    /// <summary>Story 11.3: stops capturing a removed one of the composited screens; its area turns black. Outside the gate: the compositor's clock takes it.</summary>
    public bool DropMonitor(string device)
    {
        ICaptureSource? capture;
        lock (_gate)
        {
            capture = _capture;
        }

        return capture is CompositeCapture composite && composite.Drop(device);
    }

    /// <summary>Whether it holds a Replay Buffer: Replay mode, and not the Monitor session.</summary>
    private bool HasRing => _options.Replay && !IsMonitor;

    public void Retarget(CaptureTarget target)
    {
        if (IsMonitor)
        {
            throw new InvalidOperationException("A Monitor session captures a monitor; it can't be retargeted.");
        }

        // The caller's pid is not trusted: the window itself must belong to the session's process.
        var window = WindowFinder.FromHandle(target.Window);
        if (window is null || (int)window.ProcessId != target.ProcessId)
        {
            throw new ErrorCodeException(
                ErrorCodes.RetargetOtherProcess,
                $"Window 0x{target.Window:X} does not belong to process {target.ProcessId}.");
        }

        ICaptureSource? old;
        lock (_gate)
        {
            if (_stopped)
            {
                throw new ErrorCodeException(ErrorCodes.SessionNotRunning, "The session has already stopped.");
            }

            if (IsDeviceLost())
            {
                // No capture runs on a lost device: the recovery captures the new window.
                _window = window;
                _log.LogInformation("Retargeted to window 0x{Handle:X} while the GPU device is lost; the recovery captures it", target.Window);
                return;
            }

            old = _capture;
            _capture = null;
        }

        // Disposing waits for a frame in flight, so the two captures never overlap frames.
        old?.Dispose();
        ICaptureSource next;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (IsDeviceLost() || _device is not { } device)
            {
                // Lost meanwhile: the recovery captures the new window.
                _window = window;
                return;
            }

            // Built under the gate: a recovery's teardown takes the device only under it, so it
            // can't dispose the device under this capture. No capture runs, so no frame waits here.
            next = NewCapture(target.Window, device, CaptureFps());
            _capture = next;
            _window = window;
        }

        next.Start();
        _log.LogInformation("Retargeted to window 0x{Handle:X}", target.Window);
    }

    public void SetMicMuted(bool muted, long qpc)
    {
        _micMute.Set(muted, qpc);

        // Read without the gate, which a first frame opening the encoders may hold: never block here.
        var frame = Volatile.Read(ref _anchored) ? MicMuteGate.FrameFrom(qpc, Volatile.Read(ref _anchorQpc), _qpcFrequency) : 0;
        _log.LogInformation("Mic {State} from frame {Frame}", muted ? "muted" : "unmuted", frame);
    }

    public SessionOutcome End(string reason)
    {
        bool anchored;
        ICaptureSource? capture;
        Timer? watch;

        // A first frame still opening the encoders holds the gate; let it finish.
        lock (_gate)
        {
            RequestStop(reason);
            anchored = _anchored;
            capture = _capture;
            _capture = null;
            watch = _deviceWatch;
            _deviceWatch = null;
        }

        _firstFrameTimer.Dispose();
        watch?.Dispose();

        // A forced restart or a recovery running finishes first: the chain it installs (or the one it
        // failed on) is current. A recovery sees the stop and installs nothing.
        Thread? restart;
        Thread? recovery;
        lock (_chainLock)
        {
            restart = _restartThread;
            recovery = _recoveryThread;
        }

        restart?.Join();

        // Bounded: an attempt hung on the driver must not freeze the orchestrator. It checks the stop
        // before installing and frees what it built.
        if (recovery is not null && !recovery.Join(RecoveryJoinWait))
        {
            _log.LogWarning("The GPU device recovery hadn't finished within {Seconds:0} s of the stop; the session ends without it", RecoveryJoinWait.TotalSeconds);
        }

        EncoderChain? chain;
        bool lost;
        lock (_chainLock)
        {
            chain = _current;
            lost = _deviceLost;
        }

        try
        {
            if (anchored)
            {
                _stats?.SnapshotStop();
            }

            _log.LogInformation("Stopping: {Reason}", _stopReason);
            capture?.Dispose();
            if (!anchored)
            {
                var message = _failure?.Message ?? $"Capture stopped ({_stopReason}) before the first frame.";
                return new SessionOutcome(null, _stopReason, new EngineFailure(_failure is null ? ErrorCodes.CaptureFailed : CodeOf(_failure), message));
            }

            // A failed restart left nothing to flush: its chain is gone, and the session has failed. A
            // lost device's chain can't be flushed: what its ring and Recording hold is all there is.
            if (!chain!.IsDisposed && !lost)
            {
                // No frame can arrive now; let the encoder thread offer what is queued before the flush.
                chain.CompleteVideo();

                // Capture is stopped and no frame is in flight; stop audio, then drain everything.
                chain.StopTaps();

                // Silent Tracks stop with the device taps, not after the teardown below.
                chain.StopSilenceClock();
                chain.ReportAudioFaults();
                chain.Finish(_lastFrameQpc);
                WriteStats(chain);
            }
        }
        finally
        {
            // A save still waiting has every packet it will get, and so has a Recording.
            _outputFinished = true;
            chain?.Tap?.Close();
            CloseChains();
            DisposeAll();
            RecordingTap? lostTap;
            lock (_chainLock)
            {
                lostTap = _lostTap;
                _lostTap = null;
            }

            lostTap?.Dispose();
        }

        if (_failure is null)
        {
            var seconds = (double)(_lastFrameQpc - _anchorQpc) / _qpcFrequency;
            if (IsMonitor)
            {
                _log.LogInformation("Monitor capture of {Seconds:0.0} s ended", seconds);
            }
            else if (_path is null && _keepRing)
            {
                _log.LogInformation("Draining: buffered {Seconds:0.0} s; the Replay Buffer is kept for saves until the drain is released", seconds);
            }
            else if (_path is null)
            {
                _log.LogInformation("Buffered {Seconds:0.0} s; the Replay Buffer is released once no save holds it", seconds);
            }
            else
            {
                _log.LogInformation("Wrote {Seconds:0.0} s to {Path}", seconds, _path);
            }
        }

        return new SessionOutcome(_path, _stopReason, _failure);
    }

    /// <summary>
    /// <see cref="End"/>'s teardown, but in Replay mode the ring is kept (R1): <see cref="Save"/>
    /// still cuts from it, to the end of what it holds, until <see cref="Release"/>.
    /// </summary>
    public SessionOutcome Drain(string reason)
    {
        _keepRing = HasRing;
        return End(reason);
    }

    /// <summary>Frees the ring <see cref="Drain"/> kept, once; a save still writing keeps its own lease (AD-8).</summary>
    public void Release()
    {
        if (!_keepRing || _released)
        {
            return;
        }

        _released = true;
        ReplayRingSink? ring;
        lock (_chainLock)
        {
            ring = _current?.Ring;
        }

        if (ring is not null)
        {
            ring.Dispose();
            _log.LogInformation("The drained Replay Buffer is released once no save holds it");
        }
    }

    /// <summary>
    /// In Replay mode, once the output is open: a write of the Clip up to <paramref name="qpc"/>
    /// (AD-10), bound here to the current encoder chain, whose sink and encoder it uses throughout.
    /// Its write asks the encoder for a keyframe on the first frame captured after the press, which
    /// is where the Clip ends and, once the orchestrator Resets, where the next one starts; no
    /// keyframe is asked for unless the write starts. A write that starts with a chain prepared
    /// (<see cref="PrepareChain"/>) and no Recording attached also arms the switch to it. Its own
    /// thread waits up to <see cref="SaveWait"/> for that keyframe (else the Clip ends at the
    /// press), then up to as long again for every stream to pass it by an AAC frame (encoding and
    /// the AAC FIFO lag it), cuts <c>[max(key - bufferWindow, last Reset, earlier write's claim), key)</c>
    /// from the first keyframe in it, and writes the Clip. The Buffer Window is the one in effect at
    /// the press (a resize applies at a Reset), and the cut comes from one ring generation: less
    /// than 1 s past a generation's boundary, the previous one up to the boundary, where the Clip
    /// then ends and the next one starts (<see cref="RingGenerations.Cut"/>). A chain started
    /// mid-session cuts from its first keyframe when that is under a frame before the start
    /// (<see cref="EncoderChain.SnapStart"/>). The claim (<see cref="ClipClaim"/>) keeps a Clip
    /// written while an earlier one still writes from cutting back over it. A press less than 1 s
    /// after the anchor is refused with <see cref="ErrorCodes.ClipTooShort"/>, before any keyframe
    /// is asked for. Once drained (<see cref="Drain"/>), the encoders are flushed and gone: the
    /// write asks for no keyframe, and the Clip runs to the end of the ring; after
    /// <see cref="Release"/> there is nothing to save.
    /// </summary>
    public IClipWrite? Save(long qpc)
    {
        // The anchor's release write publishes the chain's ring and encoder.
        if (!HasRing || _released || !Volatile.Read(ref _anchored))
        {
            return null;
        }

        EncoderChain? chain;
        bool lost;
        lock (_chainLock)
        {
            // A forced restart is rebuilding the chain: nothing to cut from until it is done. A lost
            // device's chain is cut as it is, torn down or not: its ring is kept (R3).
            lost = _deviceLost;
            chain = _restarting && !lost ? null : _current;
        }

        if (lost)
        {
            if (chain?.Ring is null)
            {
                return null;
            }

            if (qpc - _anchorQpc < _qpcFrequency)
            {
                throw new ErrorCodeException(ErrorCodes.ClipTooShort, "The Replay Buffer holds less than 1 s; there is nothing to save yet.");
            }

            // No encoder runs to ask for a keyframe: the Clip ends at the ring's last packet before the press.
            return new ClipWrite(this, chain, null, qpc, chain.WindowSeconds);
        }

        // A chain disposed while the output still runs is one a failed restart left: nothing to cut. A
        // drained session's is disposed too, but keeps its ring for saves.
        if (chain is null || (chain.IsDisposed && !_outputFinished) || chain.Ring is null || chain.Video is not { } video)
        {
            return null;
        }

        if (qpc - _anchorQpc < _qpcFrequency)
        {
            throw new ErrorCodeException(ErrorCodes.ClipTooShort, "The Replay Buffer holds less than 1 s; there is nothing to save yet.");
        }

        // A drained session's encoder is already disposed: never ask it for a keyframe.
        return new ClipWrite(this, chain, _outputFinished ? null : video, qpc, chain.WindowSeconds);
    }

    /// <summary>
    /// In Replay mode, or in the Monitor session, once the output is open and while it runs: a
    /// Recording from <paramref name="qpc"/> on the current chain's encoders, through its tap; null
    /// once drained or released, before the anchor, while a Recording is attached, while a chain
    /// switch is armed, while a forced restart rebuilds the chain, or while the GPU device is lost.
    /// </summary>
    public IRecordingWrite? Record(long qpc)
    {
        // The anchor's release write publishes the tap and the encoder.
        if ((!_options.Replay && !IsMonitor) || _released || _outputFinished || !Volatile.Read(ref _anchored))
        {
            return null;
        }

        EncoderChain? chain;
        lock (_chainLock)
        {
            if (_armed is not null || _restarting || _deviceLost)
            {
                return null;
            }

            chain = _current;
            if (chain is null || chain.IsDisposed || chain.Tap is not { HasWriter: false } || chain.Video is null)
            {
                return null;
            }

            // Story 12.2: the Recording keeps this chain (R12). A pending size move is cancelled, its
            // chain torn down, and the chain reserved until the writer attaches.
            CancelMoveLocked();
            _recordReservedUntil = Stopwatch.GetTimestamp() + (long)(RecordReserve.TotalSeconds * _qpcFrequency);
        }

        return new RecordingWrite(this, chain, chain.Tap!, chain.Video!, qpc);
    }

    /// <summary>
    /// Builds <paramref name="next"/>'s chain in advance, on a background thread, for the next save
    /// press (4.9); null, or the spec of the chain that is or will be current, drops a chain prepared
    /// and not yet armed. The spec already asked for changes nothing. A build waits for the anchor,
    /// for a replaced chain to be torn down and for an armed one to take over, and starts then; one
    /// a newer spec superseded is torn down, and a failed one is logged and forgotten, so the next
    /// call retries it. Nothing in file mode, once drained or stopped.
    /// </summary>
    public void PrepareChain(SessionSpec? next)
    {
        if (!HasRing)
        {
            return;
        }

        lock (_chainLock)
        {
            if (_chainsClosed || _keepRing || _outputFinished || Volatile.Read(ref _stopped))
            {
                return;
            }

            if (_deviceLost)
            {
                // Only remembered: the recovery builds it once the new device is up.
                var rebuilt = _rebuildSpec ?? (_armed ?? _current)?.Spec ?? _spec;
                _wanted = next is null || ChainKey.SameChain(next, rebuilt) ? null : next;
                return;
            }

            var current = (_armed ?? _current)?.Spec ?? _spec;
            EncoderChain? drop;
            if (next is not null && ChainKey.SameChain(next, current) && _armMove)
            {
                // Story 12.2: the same settings leave a size move alone.
                return;
            }

            if (next is null || ChainKey.SameChain(next, current))
            {
                // Null (a Monitor Recording takes the other chain) drops a size move too.
                drop = _prepared;
                _prepared = null;
                _wanted = null;
                DropMoveLocked();
            }
            else if (_wanted is { } wanted && ChainKey.SameChain(next, wanted))
            {
                return;
            }
            else
            {
                _log.LogInformation("The profile's settings need another encoder chain ({Size} at {Fps} fps, {Tracks} Tracks); it is built for the next save",
                    $"{next.Snapshot.Video.Width}x{next.Snapshot.Video.Height}", next.Snapshot.Video.Fps, next.Audio.Tracks.Count);
                _wanted = next;
                drop = _prepared;
                _prepared = null;

                // Story 12.2: the profile's chain is built at a pending move's size, and switches at a save.
                _armMove = false;
            }

            if (drop is not null)
            {
                _log.LogInformation("The prepared encoder chain is no longer needed; it is torn down");
                TearDownLocked(drop);
            }

            StartBuildLocked();
        }
    }

    /// <summary>
    /// A forced encoder restart (R4), for tests until a production trigger exists: on a thread of its
    /// own, the current chain is taken out of the capture thread's feed, the saves bound to it are
    /// waited for (bounded), it is torn down, and the same spec is rebuilt into a new generation of
    /// the same ring (AD-7: the old chain is gone before the new one is built, and no other build
    /// starts meanwhile). The new chain becomes current, then the orchestrator is told
    /// (<see cref="ISessionEvents.ChainRestarted"/>) and only then is the old chain's Recording
    /// ended, so its post-back always follows. A failed rebuild frees the ring and fails the session
    /// with <see cref="ErrorCodes.EncodeFailed"/>. Refused, with false, unless in Replay mode, anchored
    /// and live (not stopping or drained), with no switch armed or retiring and no restart running.
    /// </summary>
    /// <param name="reason">Why, for the log.</param>
    internal bool RestartChain(string reason)
    {
        lock (_gate)
        {
            lock (_chainLock)
            {
                if (!HasRing || !Volatile.Read(ref _anchored) || _stopped || _outputFinished || _keepRing || _chainsClosed || _deviceLost
                    || _armed is not null || _retiring is not null || _restarting || _current is not { IsBuilt: true, IsDisposed: false } old)
                {
                    _log.LogInformation("The encoder restart ({Reason}) was refused: the session isn't live, or a chain switch or restart is running", reason);
                    return false;
                }

                _restarting = true;
                _restartThread = new Thread(() => Restart(old, reason)) { Name = "Encoder chain restart", IsBackground = true };
                _restartThread.Start();
            }
        }

        return true;
    }

    /// <summary>
    /// The live encoder chains (AD-7): the current one, one armed, one replaced and still fed, one
    /// prepared, and one being built or rebuilt. Chains already being torn down don't count.
    /// </summary>
    public int Chains
    {
        get
        {
            lock (_chainLock)
            {
                // A lost device's chains are torn down and rebuilt as one.
                if (_deviceLost)
                {
                    return 1;
                }

                var chains = 0;
                foreach (var chain in new[] { _current, _armed, _retiring, _prepared })
                {
                    if (chain is { IsDisposed: false })
                    {
                        chains++;
                    }
                }

                // A build no longer wanted is torn down as soon as it ends; a rebuild counts once the
                // chain it replaces is gone, which counts until then.
                var building = _building && _wanted is not null;
                var rebuilding = _restarting && _current is not { IsDisposed: false };
                return chains + (building ? 1 : 0) + (rebuilding ? 1 : 0);
            }
        }
    }

    /// <summary>
    /// Switches to <paramref name="next"/>'s chain at a Reset, tearing the current one down first
    /// (AD-7, while a Monitor Recording holds a chain): <see cref="RestartChain"/>'s machinery on a
    /// thread of its own, but the new chain is <paramref name="next"/>'s, into a fresh ring (the old
    /// one goes with its chain, a save still writing keeping its lease), the frames captured meanwhile
    /// dropped, no <see cref="ISessionEvents.ChainRestarted"/> posted, and the gap from the Reset to
    /// its first keyframe raised as <see cref="CaptureSessionOptions.ResetGap"/>. Refused, with false,
    /// on the same terms as a restart, and also while a chain is prepared, built or torn down, or a
    /// Recording is attached. Orchestrator thread; never blocks on the build.
    /// </summary>
    public bool SwitchChain(SessionSpec next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (_gate)
        {
            lock (_chainLock)
            {
                if (!HasRing || !Volatile.Read(ref _anchored) || _stopped || _outputFinished || _keepRing || _chainsClosed || _deviceLost
                    || _armed is not null || _retiring is not null || _restarting || _prepared is not null || _building || _tearingDown > 0
                    || _current is not { IsBuilt: true, IsDisposed: false, Tap.HasWriter: false } old)
                {
                    _log.LogInformation("The encoder chain switch at the Reset was refused: the session isn't live, or a chain is being built, switched or recorded from; it is retried at the next Reset");
                    return false;
                }

                if (ChainKey.SameChain(next, old.Spec))
                {
                    return true;
                }

                _wanted = null;
                _restarting = true;
                _restartThread = new Thread(() => Restart(old, "settings changed while a Monitor Recording runs", next)) { Name = "Encoder chain switch", IsBackground = true };
                _restartThread.Start();
            }
        }

        return true;
    }

    /// <summary>
    /// <see cref="RestartChain"/>'s work, on its own thread; with <paramref name="switchTo"/>,
    /// <see cref="SwitchChain"/>'s: that spec's chain in a fresh ring, the old ring disposed with
    /// its chain, and the gap raised instead of the restart posted.
    /// </summary>
    private void Restart(EncoderChain old, string reason, SessionSpec? switchTo = null)
    {
        var began = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            // A lost device is the recovery's: it tears this chain down and rebuilds it.
            if (_stopped || IsDeviceLost())
            {
                lock (_chainLock)
                {
                    _restarting = false;
                }

                return;
            }

            old.Fed = false;
        }

        _log.LogInformation("Encoder restart ({Reason}) at {At:0.000} s after the anchor: the chain is torn down and rebuilt", reason, MicrosAt(began) / 1e6);

        // Kept out of the teardown: closing it ends the Recording, which must post back after the orchestrator hears of the restart.
        var tap = old.TakeTap();
        EncoderChain? next = null;
        var installed = false;
        try
        {
            // Each save bound to it cuts at its press, since its keyframe never comes; the ring's restart needs them done.
            var deadline = began + (3 * _qpcFrequency);
            while (true)
            {
                lock (_chainLock)
                {
                    if (old.Writers == 0)
                    {
                        break;
                    }
                }

                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    _log.LogWarning("A save bound to the restarted encoder chain hadn't cut within 3 s; the chain is torn down anyway");
                    break;
                }

                Thread.Sleep(10);
            }

            // A switch's new chain takes a fresh ring: the old one goes with its chain.
            old.Dispose(keepRing: switchTo is null);
            if (_options.RestartFault?.Invoke() is { } fault)
            {
                throw fault;
            }

            if (switchTo is not null)
            {
                var (encoder, codec) = ResolveEncoder(switchTo);
                next = NewChain(switchTo, encoder, codec, null, _device!);
                next.Subscribe();
                next.Build(_anchorQpc, first: false, latency: null);
            }
            else
            {
                next = NewChain(old.Spec, old.EncoderName, old.Codec, null, _device!);
                next.Subscribe();
                next.Build(_anchorQpc, first: false, latency: null, restartOf: old.Ring, window: old.WindowSeconds);
            }

            var now = Stopwatch.GetTimestamp();
            next.StartAudio(now);
            lock (_gate)
            {
                lock (_chainLock)
                {
                    if (_deviceLost)
                    {
                        // Built on the lost device: the recovery rebuilds from the old chain's ring.
                        throw new InvalidOperationException("The GPU device was lost during the encoder restart.");
                    }

                    _current = next;
                    installed = true;
                    _restarting = false;
                    CompleteMoveLocked(next);
                    if (next.Fps != old.Fps)
                    {
                        _capture?.SetFps(next.Fps);
                    }

                    StartBuildLocked();
                }
            }

            if (switchTo is not null)
            {
                _log.LogInformation("The encoder chain switched at the Reset in {Ms:0} ms, torn down first: {Size}, {Tracks} Tracks from here on",
                    (now - began) * 1000.0 / _qpcFrequency, next.OutputSize, switchTo.Audio.Tracks.Count);
                ReportSwitchGap(next);
                return;
            }

            _log.LogInformation("The encoder chain restarted in {Ms:0} ms; the frames captured meanwhile were dropped", (now - began) * 1000.0 / _qpcFrequency);
            _events.ChainRestarted(now);
        }
        catch (Exception e) when (IsDeviceLostNow() || TakeDeviceLoss(e))
        {
            // The device was lost under the restart: the recovery takes it from here (R3). The ring
            // stays for its pre-reset content, a switch's fresh one goes with its chain, and the old
            // chain's Recording stays open until the recovery is posted.
            if (!installed)
            {
                next?.Dispose(keepRing: switchTo is null);
            }

            _log.LogWarning("The encoder restart ({Reason}) stopped: the GPU device was lost ({Message}); the session recovers instead", reason, e.Message);
            lock (_gate)
            {
                lock (_chainLock)
                {
                    _restarting = false;

                    // The orchestrator already took the switch's spec: the recovery rebuilds it.
                    if (switchTo is not null)
                    {
                        _rebuildSpec = switchTo;
                    }

                    if (_lostTap is null)
                    {
                        _lostTap = tap;
                        tap = null;
                    }
                }
            }
        }
        catch (Exception e)
        {
            // Not yet current: the new chain and the ring go with the session, which fails here.
            if (!installed)
            {
                next?.Dispose();
                if (switchTo is null)
                {
                    old.Ring?.Dispose();
                }
            }

            _log.LogError("The encoder restart ({Reason}) failed: {Message}", reason, e.Message);

            // Failed first, so the session is stopped before anything may act on the disposed chain again.
            Fail(new ErrorCodeException(ErrorCodes.EncodeFailed, "The encoder restart couldn't rebuild the encoder chain: " + e.Message, e));
            lock (_gate)
            {
                lock (_chainLock)
                {
                    _restarting = false;
                }
            }
        }
        finally
        {
            // The old chain's Recording ends here, at its last packet, after the orchestrator was told.
            tap?.Dispose();
        }
    }

    /// <summary>
    /// Starts one attempt to recover from a GPU device loss (R3), on a thread of its own: the first
    /// tears the lost capture, chains and device down (<see cref="TearDownLost"/>), then each builds a
    /// new device, chain and capture (<see cref="Recover"/>). False when nothing is lost, an attempt
    /// already runs, or the session has stopped. Orchestrator thread; never blocks.
    /// </summary>
    public bool RecoverDevice()
    {
        lock (_gate)
        {
            lock (_chainLock)
            {
                if (!_deviceLost || _recovering || _stopped || _chainsClosed)
                {
                    return false;
                }

                _recovering = true;
                _suspended = false;
                var previous = _recoveryThread;
                _recoveryThread = new Thread(() => Recover(previous)) { Name = "GPU device recovery", IsBackground = true };
                _recoveryThread.Start();
            }
        }

        return true;
    }

    /// <summary>
    /// The PC is going to sleep (T-02): the session takes 8.3's lost state without a loss. It stops
    /// feeding the chains, then, on a thread of its own (<see cref="_recoveryThread"/>, which the
    /// next <see cref="Recover"/> joins), tears WGC, every chain (keeping the ring, so a save still
    /// cuts the old generation) and the device down, and ends the Recording tap at its last
    /// fragment. No frames until <see cref="RecoverDevice"/> succeeds on resume. Already lost (a GPU
    /// reset): a recovery attempt still running installs nothing, and the teardown runs once it has
    /// finished. Already suspended: true, and nothing more. False when it can't: not anchored, no Replay Buffer and not the Monitor
    /// session, or stopped, finished, draining or closed. Orchestrator thread; never blocks.
    /// </summary>
    public bool Suspend()
    {
        lock (_gate)
        {
            lock (_chainLock)
            {
                if (_suspended)
                {
                    return true;
                }

                if (!Volatile.Read(ref _anchored) || !(HasRing || IsMonitor) || _stopped || _outputFinished || _keepRing || _chainsClosed)
                {
                    return false;
                }

                // Already lost (a GPU reset's attempt may be running): it installs nothing now, and
                // the teardown below runs once it has finished.
                _suspended = true;
                _deviceLost = true;
                DropMoveLocked();
                foreach (var chain in new[] { _current, _armed, _retiring })
                {
                    chain?.Fed = false;
                }

                _log.LogInformation("The PC is going to sleep: tearing capture, the encoder chains and the device down; the Replay Buffer's ring is kept");
                var previous = _recoveryThread;
                _recoveryThread = new Thread(() => SuspendTearDown(previous)) { Name = "Suspend teardown", IsBackground = true };
                _recoveryThread.Start();
            }
        }

        return true;
    }

    /// <summary><see cref="Suspend"/>'s thread: the last attempt and a forced restart finish, then the teardown, then the Recording tap ends.</summary>
    private void SuspendTearDown(Thread? previous)
    {
        try
        {
            previous?.Join();
            Thread? restart;
            bool tornDown;
            lock (_chainLock)
            {
                restart = _restartThread;
            }

            restart?.Join();
            lock (_chainLock)
            {
                tornDown = _tornDown;
            }

            if (!tornDown && !Volatile.Read(ref _stopped))
            {
                TearDownLost();
            }
        }
        catch (Exception e)
        {
            _log.LogWarning("Tearing down for sleep failed: {Message}", e.Message);
        }

        // The Recording ends here, at its last fragment; a resume never continues it.
        RecordingTap? tap;
        lock (_chainLock)
        {
            tap = _lostTap;
            _lostTap = null;
        }

        try
        {
            tap?.Dispose();
        }
        catch (Exception e)
        {
            _log.LogWarning("Ending the Recording for sleep failed: {Message}", e.Message);
        }
    }

    /// <summary>For tests: the fault seam marks this session's device lost, as a driver reset would.</summary>
    internal void SimulateDeviceLoss()
    {
        lock (_gate)
        {
            _device?.SimulateLoss();
        }
    }

    /// <summary>
    /// One recovery attempt, on its own thread (<see cref="RecoverDevice"/>): a running forced
    /// restart and the last attempt finish first; the first attempt tears down what was lost; then
    /// <see cref="CaptureSessionOptions.RecoveryFault"/>, a new device on the Target's adapter (the
    /// Monitor's for the Monitor session), the spec rebuilt into a new generation of the same ring
    /// (AD-7: the lost chain is already gone), and new capture at its fps. Installed unless the
    /// session stopped meanwhile; then <see cref="ISessionEvents.ChainRestarted"/>, and only then is
    /// the lost chain's Recording ended, so its post-back follows. A failure frees what the attempt
    /// built (the ring kept) and posts <see cref="ISessionEvents.DeviceLost"/>; a stop posts nothing.
    /// </summary>
    private void Recover(Thread? previous)
    {
        previous?.Join();
        var began = Stopwatch.GetTimestamp();
        Thread? restart;
        lock (_chainLock)
        {
            restart = _restartThread;
        }

        restart?.Join();
        D3D11Device? device = null;
        ICaptureSource? capture = null;
        EncoderChain? next = null;
        var installed = false;
        var stopped = false;
        Exception? failure = null;
        try
        {
            bool tornDown;
            lock (_chainLock)
            {
                tornDown = _tornDown;
            }

            if (!tornDown)
            {
                TearDownLost();
            }

            EncoderChain lost;
            SessionSpec spec;
            WindowInfo? window;
            lock (_gate)
            {
                stopped = _stopped;
                window = _window;
                lock (_chainLock)
                {
                    lost = _current!;
                    spec = _rebuildSpec ?? lost.Spec;
                }
            }

            if (stopped)
            {
                return;
            }

            if (_options.RecoveryFault?.Invoke() is { } fault)
            {
                throw fault;
            }

            device = _monitor is { } monitor
                ? D3D11Device.CreateForMonitor(monitor.Handle, _loggers.CreateLogger<D3D11Device>())
                : D3D11Device.Create(window!.Handle, _loggers.CreateLogger<D3D11Device>());
            var (encoder, codec) = ReferenceEquals(spec, lost.Spec) ? (lost.EncoderName, lost.Codec) : ResolveEncoder(spec);
            next = NewChain(spec, encoder, codec, null, device);
            next.Subscribe();

            // The same ring, in a new generation: what it held before the reset stays saveable. One a
            // switch under way had already freed is replaced by a fresh one.
            var ring = HasRing && lost.Ring is { IsDisposed: false } kept ? kept : null;
            next.Build(_anchorQpc, first: false, latency: null, restartOf: ring, window: ring is null ? 0 : lost.WindowSeconds);
            if (IsMonitor)
            {
                // WGC, else Desktop Duplication, as at Open; started here and installed below.
                capture = StartMonitorCapture(device, next.Fps, c => c.Start());
            }
            else
            {
                capture = NewCapture(window!.Handle, device, next.Fps);
                capture.Start();
            }
            var now = Stopwatch.GetTimestamp();
            next.StartAudio(now);
            var asleep = false;
            lock (_gate)
            {
                stopped = _stopped;
                lock (_chainLock)
                {
                    asleep = _suspended;
                }

                if (asleep && !stopped)
                {
                    _log.LogInformation("The PC went to sleep during the recovery attempt: nothing is installed; the resume rebuilds");
                }

                if (!stopped && !asleep)
                {
                    if (!ReferenceEquals(_window, window))
                    {
                        throw new InvalidOperationException("The Target's window changed during the recovery; the next attempt captures the new one.");
                    }

                    lock (_chainLock)
                    {
                        _device = device;
                        _capture = capture;
                        _current = next;
                        _deviceLost = false;
                        _tornDown = false;
                        _recovering = false;
                        _rebuildSpec = null;
                        installed = true;

                        // A chain the profile asked for meanwhile is built on the new device.
                        StartBuildLocked();
                    }
                }
            }

            if (stopped || asleep)
            {
                return;
            }

            var adapter = device.AdapterName;
            device = null;
            capture = null;
            next = null;
            if (_options.GpuPriority is { } priority)
            {
                SetGpuPriority(priority);
            }

            _log.LogInformation("Recovered from the GPU device loss in {Ms:0} ms: a new device on {Adapter}, the encoder chain rebuilt; the frames captured meanwhile were dropped",
                (now - began) * 1000.0 / _qpcFrequency, adapter);
            _events.ChainRestarted(now);

            // The lost chain's Recording ends here, at its last packet, after the orchestrator was told.
            RecordingTap? tap;
            lock (_chainLock)
            {
                tap = _lostTap;
                _lostTap = null;
            }

            tap?.Dispose();
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            // What this attempt built and didn't install; the ring stays for the next attempt.
            capture?.Dispose();
            next?.Dispose(keepRing: true);
            device?.Dispose();
            if (!installed)
            {
                lock (_chainLock)
                {
                    _recovering = false;
                }
            }
        }

        if (failure is null)
        {
            return;
        }

        lock (_gate)
        {
            stopped = _stopped;
        }

        // Installed already (only closing the old Recording threw): the session has recovered.
        if (installed)
        {
            _log.LogWarning("Ending the lost chain's Recording after the GPU recovery failed: {Message}", failure.Message);
            return;
        }

        _log.LogWarning("The recovery from the GPU device loss failed: {Message}", failure.Message);
        if (!stopped)
        {
            _events.DeviceLost(failure);
        }
    }

    /// <summary>
    /// The first recovery attempt's teardown, AD-7's order: WGC, then every chain (builds and
    /// teardowns running on the lost device finish first; the current one keeps its ring, and its
    /// Recording tap is kept open), then the device. The spec rebuilt is an armed chain's, whose
    /// switch the orchestrator already took, else the current one's.
    /// </summary>
    private void TearDownLost()
    {
        var began = Stopwatch.GetTimestamp();
        ICaptureSource? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }

        // Disposing waits for a frame in flight, which takes the gate: never under it.
        capture?.Dispose();
        EncoderChain? armed;
        EncoderChain? retiring;
        EncoderChain? prepared;
        lock (_gate)
        {
            lock (_chainLock)
            {
                armed = _armed;
                _armed = null;
                retiring = _retiring;
                _retiring = null;
                prepared = _prepared;
                _prepared = null;
                _rebuildSpec = armed?.Spec ?? _rebuildSpec ?? _current!.Spec;
            }
        }

        // Builds, teardowns and retirements already running finish; none starts while the device is lost.
        while (true)
        {
            List<Thread> workers;
            lock (_chainLock)
            {
                workers = [.. _workers];
                _workers.Clear();
            }

            if (workers.Count == 0)
            {
                break;
            }

            foreach (var worker in workers)
            {
                worker.Join();
            }
        }

        EncoderChain current;
        RecordingTap? extra = null;
        EncoderChain? built;
        lock (_chainLock)
        {
            // A build that finished meanwhile.
            built = _prepared;
            _prepared = null;
            current = _current!;
            var tap = current.TakeTap();
            if (_lostTap is null)
            {
                _lostTap = tap;
            }
            else
            {
                extra = tap;
            }
        }

        try
        {
            extra?.Dispose();
        }
        catch (Exception e)
        {
            _log.LogWarning("Closing a second lost Recording tap failed: {Message}", e.Message);
        }

        // Each save bound to it cuts at its press, since its keyframe never comes; the ring's restart needs them done.
        var deadline = began + (3 * _qpcFrequency);
        while (!Volatile.Read(ref _stopped))
        {
            lock (_chainLock)
            {
                if (current.Writers == 0)
                {
                    break;
                }
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                _log.LogWarning("A save bound to the lost encoder chain hadn't cut within 3 s; the chain is torn down anyway");
                break;
            }

            Thread.Sleep(10);
        }

        // Each in its own step: one that throws on the lost device never leaks the others.
        void Step(string what, Action dispose)
        {
            try
            {
                dispose();
            }
            catch (Exception e)
            {
                _log.LogWarning("Tearing down the lost {What} failed: {Message}", what, e.Message);
            }
        }

        Step("encoder chain", () => current.Dispose(keepRing: true));
        Step("armed encoder chain", () => armed?.Dispose());
        Step("replaced encoder chain", () => retiring?.Dispose());
        Step("prepared encoder chain", () => prepared?.Dispose());
        Step("built encoder chain", () => built?.Dispose());
        D3D11Device? device;
        lock (_gate)
        {
            device = _device;
            _device = null;
        }

        Step("device", () => device?.Dispose());
        lock (_chainLock)
        {
            _tornDown = true;
        }

        _log.LogInformation("The lost GPU device, its capture and its encoder chains were torn down in {Ms:0} ms; the Replay Buffer's ring is kept",
            (Stopwatch.GetTimestamp() - began) * 1000.0 / _qpcFrequency);
    }

    /// <summary>Whether the device is lost; under the gate.</summary>
    private bool IsDeviceLost()
    {
        lock (_chainLock)
        {
            return _deviceLost;
        }
    }

    /// <summary>Whether the device is lost; takes the gate.</summary>
    private bool IsDeviceLostNow()
    {
        lock (_gate)
        {
            return IsDeviceLost();
        }
    }


    /// <summary>
    /// The device watchdog's tick: a device that reports itself removed or reset is a loss, as a
    /// failure carrying the reason would be. Skipped once stopped, lost or torn down.
    /// </summary>
    private void WatchDevice()
    {
        int reason;
        lock (_gate)
        {
            if (_stopped || _device is not { } device || IsDeviceLost())
            {
                return;
            }

            reason = device.RemovedReason;
        }

        if (reason < 0)
        {
            Fail(new COMException($"The GPU device was removed or reset (0x{reason:X8}).", reason));
        }
    }

    /// <summary>
    /// Takes <paramref name="e"/> as a GPU device loss when it is one: a later failure while the
    /// device is lost is swallowed; a first one carrying <see cref="D3D11Device.DeviceRemoved"/> or
    /// <see cref="D3D11Device.DeviceReset"/> in its chain, or with the device reporting itself lost,
    /// stops feeding the chains and posts <see cref="ISessionEvents.DeviceLost"/>, when the session
    /// can recover: anchored, holding a Replay Buffer or the Monitor session's, and live. False
    /// leaves it to <see cref="Fail"/>.
    /// </summary>
    private bool TakeDeviceLoss(Exception e)
    {
        lock (_gate)
        {
            lock (_chainLock)
            {
                if (_deviceLost)
                {
                    _log.LogInformation("A failure on the lost GPU device was ignored: {Message}", e.Message);
                    return true;
                }

                if (!Volatile.Read(ref _anchored) || !(HasRing || IsMonitor) || _stopped || _outputFinished || _keepRing || _chainsClosed)
                {
                    return false;
                }
            }

            if (!IsDeviceLoss(e))
            {
                return false;
            }

            lock (_chainLock)
            {
                _deviceLost = true;
                DropMoveLocked();
                foreach (var chain in new[] { _current, _armed, _retiring })
                {
                    chain?.Fed = false;
                }
            }
        }

        _log.LogWarning("{Code}: the GPU device was lost ({Message}); nothing is encoded until the session recovers", ErrorCodes.GpuReset, e.Message);
        _events.DeviceLost(e);
        return true;
    }

    /// <summary>Whether <paramref name="e"/> is a device loss: a removed or reset HRESULT in its chain, or the device reporting itself lost. Under the gate.</summary>
    private bool IsDeviceLoss(Exception e)
    {
        for (var inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner.HResult is D3D11Device.DeviceRemoved or D3D11Device.DeviceReset)
            {
                return true;
            }
        }

        return _device?.IsLost ?? false;
    }

    /// <summary>Frees what <see cref="Open"/> managed to open, when it threw.</summary>
    public void Abandon()
    {
        ICaptureSource? capture;
        Timer? watch;
        lock (_gate)
        {
            _stopped = true;
            capture = _capture;
            _capture = null;
            watch = _deviceWatch;
            _deviceWatch = null;
        }

        _firstFrameTimer.Dispose();
        watch?.Dispose();
        capture?.Dispose();
        DisposeAll();
    }

    internal static string CodeOf(Exception e) => e switch
    {
        EngineFailure f => f.Code,
        ErrorCodeException c => c.Code,
        _ => ErrorCodes.EncodeFailed,
    };

    /// <summary>A game window's WGC capture on <paramref name="device"/> at <paramref name="fps"/>, not started; a WGC failure throws, with no fallback.</summary>
    private WindowCapture NewCapture(nint window, D3D11Device device, int fps)
    {
        ThrowWgcFault();
        return Wire(new WindowCapture(device, window, fps, OnFrame, _loggers.CreateLogger<WindowCapture>()));
    }

    /// <summary>
    /// The Monitor session's capture on <paramref name="device"/>, started by <paramref name="start"/>
    /// (<see cref="StartInstalled"/> at Open; a plain start in a GPU-reset recovery, which installs it
    /// afterwards): WGC's monitor item, or, when creating or starting it throws, Desktop Duplication
    /// of the same monitor on the same device (AD-12), raising
    /// <see cref="CaptureSessionOptions.CaptureFallback"/> once per capture. Desktop Duplication
    /// failing too throws.
    /// </summary>
    private ICaptureSource StartMonitorCapture(D3D11Device device, int fps, Action<ICaptureSource> start)
    {
        if (_composite is not null)
        {
            var composite = NewComposite(device, fps);
            try
            {
                start(composite);
            }
            catch
            {
                composite.Dispose();
                throw;
            }

            if (composite.Sources.Where(s => s.Api == "desktop-duplication").Select(s => s.Placement.Monitor.DeviceName).ToList() is { Count: > 0 } duplicated)
            {
                _options.CaptureFallback?.Invoke(
                    $"Windows Graphics Capture couldn't capture {string.Join(", ", duplicated)}; the Monitor Recording uses Desktop Duplication for it instead.");
            }

            return composite;
        }

        var monitor = _monitor!;
        WindowCapture? wgc = null;
        try
        {
            ThrowWgcFault();
            wgc = Wire(WindowCapture.ForMonitor(device, monitor.Handle, fps, OnFrame, _loggers.CreateLogger<WindowCapture>()));
            start(wgc);
            CaptureApi = "wgc";
            return wgc;
        }
        catch (Exception e)
        {
            wgc?.Dispose();
            _log.LogWarning("WGC could not capture monitor {Device} ({Message}); falling back to Desktop Duplication", monitor.DeviceName, e.Message);
        }

        var duplication = Wire(new DesktopDuplicationCapture(device, monitor.Handle, fps, OnFrame, _loggers.CreateLogger<DesktopDuplicationCapture>()));
        try
        {
            start(duplication);
        }
        catch
        {
            duplication.Dispose();
            throw;
        }

        CaptureApi = "desktop-duplication";
        _options.CaptureFallback?.Invoke(
            $"Windows Graphics Capture couldn't capture monitor {monitor.DeviceName}; the Monitor Recording uses Desktop Duplication instead.");
        return duplication;
    }

    /// <summary>
    /// Makes <paramref name="capture"/> the session's capture, then starts it, so a <c>Closed</c> it
    /// raises at once isn't taken for a replaced capture's; a failed start uninstalls it and throws.
    /// </summary>
    private void StartInstalled(ICaptureSource capture)
    {
        lock (_gate)
        {
            _capture = capture;
        }

        try
        {
            capture.Start();
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_capture, capture))
                {
                    _capture = null;
                }
            }

            throw;
        }
    }

    /// <summary>Spike 11.1: the composited capture of <see cref="_composite"/> on <paramref name="device"/>, not started.</summary>
    private CompositeCapture NewComposite(D3D11Device device, int fps)
    {
        var forced = _options.DuplicationScreens;
        var composite = Wire(new CompositeCapture(
            device, _composite!, fps,
            p => _options.CompositeDuplication || forced?.Contains(p.Monitor.DeviceName, StringComparer.OrdinalIgnoreCase) == true,
            OnFrame, _loggers.CreateLogger<CompositeCapture>()));
        CaptureApi = _options.CompositeDuplication ? "composite-desktop-duplication" : "composite";
        return composite;
    }

    private void ThrowWgcFault()
    {
        if (_options.WgcFault?.Invoke() is { } fault)
        {
            throw fault;
        }
    }

    private int CaptureFps()
    {
        lock (_chainLock)
        {
            return _current?.Fps ?? _spec.Snapshot.Video.Fps;
        }
    }

    private T Wire<T>(T capture)
        where T : ICaptureSource
    {
        capture.Closed += () => OnClosed(capture);
        capture.Failed += Fail;
        return capture;
    }

    private void OnFrame(long qpc, nint texture, VideoSize contentSize)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (!_anchored)
            {
                StartOutput(qpc, contentSize);
            }

            // A lost device takes no frames: the recovery's new capture brings them again.
            if (_deviceLost)
            {
                return;
            }

            // For tests: a secure desktop delivers no frames; the pacer repeats the last one.
            if (_options.StarveFrames?.Invoke() == true)
            {
                return;
            }

            _stats?.Cadence.Record(qpc);
            _lastFrameQpc = qpc;
            if (_options.Stages == CaptureStages.Wgc)
            {
                return;
            }

            if (HasRing)
            {
                WatchSourceSize(qpc, contentSize);
            }

            // The first frame captured after an armed press: the new chain's first, and the one the
            // old chain's forced keyframe lands on, so the next Clip starts where this one ends.
            if (_armed is { } armed && qpc > _armedQpc)
            {
                SwitchTo(armed, qpc);
            }

            if (_retiring is { Fed: true } retiring)
            {
                retiring.Draw(qpc, texture, contentSize);
            }

            // A forced restart drops the frames captured while it rebuilds the chain.
            if (_current is { Fed: true } current)
            {
                current.Draw(qpc, texture, contentSize);
            }
        }
    }

    /// <summary>
    /// The encoder and codec for <paramref name="spec"/>: the forced encoder, else the probe's for
    /// the snapshot's codec, else (a codec this PC can't encode, from a hand-edited or copied
    /// document) H.264's.
    /// </summary>
    private (string Encoder, VideoCodec Codec) ResolveEncoder(SessionSpec spec)
    {
        if (_options.Encoder is { } forced)
        {
            var forcedCodec = EncoderProbe.CodecOf(forced)
                ?? throw new EngineFailure(ErrorCodes.EncodeFailed, $"'{forced}' is not an H.264, HEVC or AV1 encoder in this FFmpeg build.");
            _log.LogInformation("Encoder forced to {Encoder} ({Codec})", forced, forcedCodec);
            return (forced, forcedCodec);
        }

        var codec = spec.Snapshot.Video.Codec;
        string? encoder;
        try
        {
            encoder = _encoders.EncoderFor(codec);
        }
        catch (Exception e) when (codec != VideoCodec.H264)
        {
            // Only H.264's own probe failing fails the session; any other codec's falls back.
            _log.LogWarning("The {Codec} encoder probe failed: {Message}", codec, e.Message);
            encoder = null;
        }

        if (encoder is not null)
        {
            return (encoder, codec);
        }

        _log.LogWarning("No {Codec} encoder works on this PC; the session falls back to H.264", codec);
        return _encoders.EncoderFor(VideoCodec.H264) is { } fallback
            ? (fallback, VideoCodec.H264)
            : throw new EngineFailure(ErrorCodes.EncodeFailed, "No H.264 encoder works on this PC.");
    }

    /// <summary>The first frame is the session anchor: every stream's PTS 0 is its QPC. Under the gate.</summary>
    private void StartOutput(long anchorQpc, VideoSize contentSize)
    {
        _anchorQpc = anchorQpc;
        var chain = _current!;
        _sourceSize = contentSize;
        SizedToSource(chain);
        _log.LogInformation("Anchor QPC {Anchor}; output {Size}, content {Content}", anchorQpc, chain.OutputSize, contentSize);

        _stats?.SnapshotAnchor();
        chain.Build(anchorQpc, first: true, _stats?.Latency);

        // The hub's streams have run since Open; each device takes blocks from here, its held ones
        // replayed first, cut at the anchor once, and fanned out to every Track that holds it.
        chain.StartAudio(anchorQpc);

        // A release write: SetMicMuted, Save and Record read the anchor without the gate.
        Volatile.Write(ref _anchored, true);

        // Only a session that can recover watches its device (R3).
        if (HasRing || IsMonitor)
        {
            _deviceWatch = new Timer(_ => WatchDevice(), null, _options.DeviceWatchInterval, _options.DeviceWatchInterval);
        }

        // A chain asked for before the anchor is built from here.
        lock (_chainLock)
        {
            StartBuildLocked();
        }

        _options.Anchored?.Invoke(anchorQpc);

        // Only posts: Record can succeed from here.
        _events.Anchored(anchorQpc);
    }

    /// <summary>
    /// Makes the armed chain current at the frame captured at <paramref name="qpc"/>; the old one is
    /// fed until every save bound to it has cut, then torn down. Under the gate.
    /// </summary>
    private void SwitchTo(EncoderChain armed, long qpc)
    {
        lock (_chainLock)
        {
            var old = _current!;
            _current = armed;
            _armed = null;
            CompleteMoveLocked(armed);
            _retiring = old;
            old.Retiring = true;
            old.Successor = armed;
            TakeRetirementLocked(old);
        }

        _log.LogInformation("The encoder chain switched at the frame {At:0.000} s after the anchor: {Size}, {Tracks} Tracks from here on",
            MicrosAt(qpc) / 1e6, armed.OutputSize, armed.Spec.Audio.Tracks.Count);
    }

    /// <summary>
    /// Arms the prepared chain at the press <paramref name="qpc"/>, for a write bound to the
    /// current <paramref name="chain"/> that has just asked for its keyframe: when one is ready,
    /// nothing is armed or retiring, no Recording is attached and the session is live. Its audio
    /// starts at the press; its video at the first frame captured after <paramref name="armQpc"/>,
    /// read under the gate, which is where the old chain's keyframe must then be asked for. Returns
    /// it, or null. Orchestrator thread.
    /// </summary>
    private EncoderChain? TryArm(EncoderChain chain, long qpc, out long armQpc)
    {
        EncoderChain? armed = null;
        armQpc = qpc;
        lock (_gate)
        {
            if (_stopped)
            {
                return null;
            }

            // Under the gate: no frame is being handed to the chains, so every frame captured after
            // this instant reaches the new chain, and none at or before it does.
            armQpc = Math.Max(qpc, Stopwatch.GetTimestamp());

            lock (_chainLock)
            {
                if (_prepared is { } prepared && _armed is null && _retiring is null && !_restarting && !_deviceLost && ReferenceEquals(chain, _current)
                    && chain.Tap is { HasWriter: false } && !_outputFinished && !_keepRing && !_chainsClosed)
                {
                    _prepared = null;
                    _wanted = null;
                    _armed = prepared;
                    _armedQpc = armQpc;
                    armed = prepared;
                }
            }
        }

        if (armed is null)
        {
            return null;
        }

        // Audio flows into it only once its taps are published, here.
        armed.StartAudio(qpc);
        _log.LogInformation("The save press {At:0.000} s after the anchor switches the Replay Buffer to the prepared encoder chain",
            MicrosAt(qpc) / 1e6);
        return armed;
    }

    /// <summary>Ends a save's registration on <paramref name="chain"/>, once it has cut or failed; the switching save first records its Clip's end.</summary>
    private void EndWriter(EncoderChain chain, long? switchEndUs)
    {
        lock (_chainLock)
        {
            if (switchEndUs is { } end)
            {
                chain.SwitchEndUs = end;
            }

            chain.Writers--;
            TakeRetirementLocked(chain);
        }
    }

    /// <summary>Starts <paramref name="chain"/>'s retirement once it is replaced and no save is bound to it. Under the chain lock.</summary>
    private void TakeRetirementLocked(EncoderChain chain)
    {
        if (!chain.Retiring || chain.Writers > 0 || chain.RetireStarted || _chainsClosed)
        {
            return;
        }

        chain.RetireStarted = true;
        StartWorkerLocked("Encoder chain retirement", () => Retire(chain));
    }

    /// <summary>
    /// On a background thread: takes the replaced chain out of the capture thread's feed and tears
    /// it down, with no flush; checks the new chain started within a frame of the old Clip's end
    /// (else <c>reset-gap</c>); sets the capture to the new chain's fps; then starts a build asked
    /// for meanwhile.
    /// </summary>
    private void Retire(EncoderChain old)
    {
        lock (_gate)
        {
            old.Fed = false;
        }

        old.Dispose();
        _log.LogInformation("The replaced encoder chain is torn down");
        var next = old.Successor!;
        CheckGap(old, next);
        if (next.Fps != old.Fps)
        {
            lock (_gate)
            {
                _capture?.SetFps(next.Fps);
            }
        }

        lock (_gate)
        {
            lock (_chainLock)
            {
                if (ReferenceEquals(_retiring, old))
                {
                    _retiring = null;
                }

                StartBuildLocked();
            }
        }
    }

    /// <summary>
    /// Waits up to <see cref="SaveWait"/> for <paramref name="next"/>'s first keyframe; when it is
    /// more than one of its frames after the switching Clip's end, raises <c>reset-gap</c> with the gap.
    /// </summary>
    private void CheckGap(EncoderChain old, EncoderChain next)
    {
        var end = old.SwitchEndUs;
        if (end == long.MinValue || next.Ring is not { } ring)
        {
            return;
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(SaveWait.TotalSeconds * _qpcFrequency);
        while (ring.FirstKeyUs == long.MaxValue && !_chainsClosed && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(10);
        }

        if (_chainsClosed)
        {
            return;
        }

        var first = ring.FirstKeyUs;
        if (first <= end + next.FrameUs)
        {
            return;
        }

        var message = first == long.MaxValue
            ? $"After your last saved Clip, the video didn't restart for {(MicrosAt(Stopwatch.GetTimestamp()) - end) / 1000} ms; the next Clip starts when it does."
            : $"After your last saved Clip, the video restarted {(first - end) / 1000} ms late; the next Clip starts that much later.";
        _log.LogWarning("{Code}: {Message}", ErrorCodes.ResetGap, message);
        _options.ResetGap?.Invoke(message);
    }

    /// <summary>
    /// After a switch at a Reset (<see cref="SwitchChain"/>): waits up to <see cref="SaveWait"/> for
    /// <paramref name="next"/>'s first keyframe, and raises <c>reset-gap</c> with the gap from the
    /// last Reset to it (the frames captured while the old chain was torn down and the new one built
    /// are in no Clip).
    /// </summary>
    private void ReportSwitchGap(EncoderChain next)
    {
        if (next.Ring is not { } ring)
        {
            return;
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(SaveWait.TotalSeconds * _qpcFrequency);
        // End stops the session, then joins this thread: it must not wait out the deadline.
        while (ring.FirstKeyUs == long.MaxValue && !_chainsClosed && !_outputFinished && !Volatile.Read(ref _stopped) && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(10);
        }

        if (_chainsClosed || _outputFinished || Volatile.Read(ref _stopped))
        {
            return;
        }

        var reset = Volatile.Read(ref _resetUs);
        var first = ring.FirstKeyUs;
        var message = first == long.MaxValue
            ? "Your new video settings took effect after your last saved Clip, while a Monitor Recording runs; the video didn't restart within 1 s, so the next Clip starts when it does."
            : $"Your new video settings took effect after your last saved Clip, while a Monitor Recording runs; the next Clip starts {(reset == long.MinValue ? 0 : Math.Max(0, first - reset)) / 1000} ms after the last one ended.";
        _log.LogWarning("{Code}: {Message}", ErrorCodes.ResetGap, message);
        _options.ResetGap?.Invoke(message);
    }

    /// <summary>
    /// Starts the build of the spec asked for, when there is one, the session has anchored, it is
    /// not built yet, and no build, armed chain, retirement or forced restart is in the way. Under
    /// the chain lock.
    /// </summary>
    private void StartBuildLocked()
    {
        if (_wanted is not { } spec || _chainsClosed || !Volatile.Read(ref _anchored) || _building || _restarting || _deviceLost
            || _prepared is not null || _armed is not null || _retiring is not null || _tearingDown > 0)
        {
            return;
        }

        _building = true;
        StartWorkerLocked("Encoder chain build", () => Build(spec));
    }

    /// <summary>
    /// On a background thread: builds <paramref name="spec"/>'s chain, idle, and hands it over as
    /// prepared when it is still the one asked for and the session is live; otherwise it is torn
    /// down. A failure is logged and forgets the request, so the next one retries.
    /// </summary>
    private void Build(SessionSpec spec)
    {
        EncoderChain? chain = null;
        try
        {
            var (encoder, codec) = ResolveEncoder(spec);
            chain = NewChain(spec, encoder, codec, null, _device!);
            chain.Subscribe();
            chain.Build(_anchorQpc, first: false, latency: null);
            _log.LogInformation("The encoder chain for the next save is built: {Size} at {Fps} fps, {Tracks} Tracks",
                chain.OutputSize, chain.Fps, spec.Audio.Tracks.Count);
        }
        catch (Exception e)
        {
            _log.LogWarning("The encoder chain for the profile's new settings couldn't be built; the session keeps its own until the next Reset or settings change: {Message}", e.Message);
            chain?.Dispose();
            chain = null;
        }

        lock (_chainLock)
        {
            _building = false;
            // One built on a device lost meanwhile is torn down; the recovery builds it again.
            if (chain is not null && !_chainsClosed && !_outputFinished && !Volatile.Read(ref _stopped) && !_deviceLost
                && _wanted is { } wanted && ChainKey.SameChain(wanted, spec))
            {
                _prepared = chain;

                // Story 12.2: a move's chain arms at once, without a save.
                TryStartArmLocked();
            }
            else if (chain is not null)
            {
                // Superseded meanwhile: torn down before the newer spec's build starts.
                TearDownLocked(chain);
            }
            else if (ReferenceEquals(_wanted, spec))
            {
                _wanted = null;

                // A move whose chain couldn't be built is forgotten: the size must hold again.
                DropMoveLocked();
            }

            // A newer spec asked for while this one built.
            StartBuildLocked();
        }
    }

    /// <summary>Tears <paramref name="chain"/> down on a background thread; no build starts until it is gone. Under the chain lock.</summary>
    private void TearDownLocked(EncoderChain chain)
    {
        _tearingDown++;
        StartWorkerLocked("Encoder chain teardown", () =>
        {
            try
            {
                chain.Dispose();
            }
            finally
            {
                lock (_chainLock)
                {
                    _tearingDown--;
                    StartBuildLocked();
                }
            }
        });
    }

    /// <summary>Starts a background thread the session's End waits for. Under the chain lock.</summary>
    private void StartWorkerLocked(string name, Action work)
    {
        _workers.RemoveAll(t => !t.IsAlive);
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                _log.LogError(e, "{Name} failed", name);
            }
        })
        {
            Name = name,
            IsBackground = true,
        };
        _workers.Add(thread);
        thread.Start();
    }

    /// <summary>
    /// End's side of the chains: no build, teardown or retirement starts from here, those running
    /// are waited for, and the prepared and armed chains are torn down. The replaced chain still
    /// fed, if any, goes in <see cref="DisposeAll"/>.
    /// </summary>
    private void CloseChains()
    {
        List<Thread> workers;
        EncoderChain? prepared;
        lock (_chainLock)
        {
            _chainsClosed = true;
            workers = [.. _workers];
            _workers.Clear();
            prepared = _prepared;
            _prepared = null;
            _wanted = null;
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }

        EncoderChain? armed;
        lock (_gate)
        {
            lock (_chainLock)
            {
                armed = _armed;
                _armed = null;
            }
        }

        prepared?.Dispose();
        armed?.Dispose();
    }

    /// <summary>Sets the device's GPU thread priority once (AD-7); a failure is logged and the run goes on.</summary>
    private void SetGpuPriority(int priority)
    {
        int hr;
        int? readBack;
        try
        {
            hr = _device!.SetGpuThreadPriority(priority, out readBack);
        }
        catch (Exception e)
        {
            hr = e.HResult;
            readBack = null;
        }

        var hex = $"0x{hr:X8}";
        var succeeded = hr >= 0;
        if (succeeded)
        {
            _log.LogInformation("GPU thread priority set to {Priority} ({Hresult}); device reports {ReadBack}", priority, hex, readBack);
        }
        else
        {
            _log.LogWarning("{Code}: SetGPUThreadPriority({Priority}) failed with {Hresult}; capturing without it",
                ErrorCodes.GpuPriorityFailed, priority, hex);
        }

        _stats?.GpuPriority = new GpuPriorityReport(priority, hex, succeeded, readBack);
    }

    /// <summary>Writes the stats file once the trailer is written.</summary>
    private void WriteStats(EncoderChain chain)
    {
        var encoder = chain.Video;
        if (_stats is null || _options.StatsPath is not { } path || encoder is null
            || chain.AudioEncoders.Count != chain.Spec.Audio.Tracks.Count || chain.AudioTimelines.Count != chain.AudioTracks.Count)
        {
            return;
        }

        var cadence = _stats.Cadence;
        var arrived = cadence.Frames;
        var stepped = arrived - cadence.NonIncreasing; // frames that advanced the capture clock
        var span = cadence.SpanSeconds;
        var slots = stepped + cadence.MissedSlots;
        var paced = chain.Paced;
        var pacer = paced?.Pacer;
        var emitted = pacer?.EmittedSlots ?? 0;
        var repeated = pacer?.RepeatedSlots ?? 0;
        var video = new VideoReport(
            chain.OutputSize.Width,
            chain.OutputSize.Height,
            arrived,
            encoder.EncodedFrames,
            repeated,
            (paced?.QueueDrops ?? 0) + encoder.PoolMisses,
            cadence.NonIncreasing,
            cadence.MissedSlots,
            cadence.Gaps,
            cadence.MaxStepMilliseconds,
            span > 0 ? (stepped - 1) / span : 0,
            emitted > 0 ? repeated * 100.0 / emitted : 0,
            slots > 0 ? cadence.MissedSlots * 100.0 / slots : 0,
            pacer?.FreshSlots ?? 0,
            pacer?.SkippedSlots ?? 0,
            pacer?.HeldReplaced ?? 0);

        // Every device, with the hub's stamp and stream readouts, and the samples encoded by the
        // first Track that holds it.
        var audio = new List<AudioReport>();
        for (var i = 0; i < chain.AudioTracks.Count; i++)
        {
            var track = chain.AudioTracks[i];
            if (track.Subscription is AudioDeviceSubscription { Diagnostics: var diagnostics })
            {
                var role = track.Device.Flow == SessionFlow.Output ? "output" : "input";
                var first = chain.Spec.Audio.FirstTrackOf(i);
                var samples = first >= 0 ? chain.AudioEncoders[first].SamplesWritten : 0;
                audio.Add(CaptureStatsCollector.Report(role, track.Device.FriendlyName, diagnostics.Stamps, diagnostics.Stream, chain.AudioTimelines[i], samples));
            }
        }

        var report = new CaptureStatsReport(
            CaptureStatsReport.CurrentSchema,
            DateTimeOffset.Now,
            _path ?? "",
            _window?.Title ?? "",
            _window?.ProcessName,
            _device?.AdapterName ?? "",
            _options.RequestedSeconds,
            _stopReason,
            (double)(_lastFrameQpc - _anchorQpc) / _qpcFrequency,
            _stats.GpuPriority,
            new EncoderReport(
                _options.Stages.ToString().ToLowerInvariant(),
                _options.NvencPreset,
                _options.NvencTune,
                _options.Color == ColorPath.X2 ? "x2" : "x1",
                paced is not null),
            video,
            CaptureStatsCollector.Report(_stats.Latency, encoder.FlushedPackets),
            _stats.Resources(),
            audio);

        try
        {
            CaptureStatsCollector.Write(path, report);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Fail(new EngineFailure(ErrorCodes.StatsWriteFailed, $"Could not write the stats file '{path}': {e.Message}"));
            return;
        }

        _log.LogInformation("Stats written to {Path}: {Encoded} frames encoded ({Fresh} new), latency p99 {P99:0.0} ms",
            path, video.FramesEncoded, video.FreshSlots, report.Latency.P99Ms);
    }

    private void OnClosed(ICaptureSource capture)
    {
        lock (_gate)
        {
            // A window left by a retarget, or a session already stopping, doesn't end anything.
            if (_stopped || !ReferenceEquals(capture, _capture))
            {
                return;
            }

            if (_options.FollowWindow && !IsMonitor)
            {
                // The closed capture stays until a retarget or the end disposes it.
                _log.LogInformation("Window 0x{Handle:X} closed; the session waits for a retarget", _window?.Handle ?? 0);
            }
            else
            {
                RequestStop("window closed");
            }
        }

        _events.Closed("window closed");
    }

    private void OnFirstFrameTimeout()
    {
        lock (_gate)
        {
            if (_anchored || _stopped)
            {
                return;
            }
        }

        Fail(new EngineFailure(ErrorCodes.CaptureFailed,
            $"No frame arrived from the window within {_options.FirstFrameTimeout.TotalSeconds:0} s."));
    }

    /// <summary>
    /// Records the first failure and stops; the orchestrator is told, and ends the session. A GPU
    /// device loss the session can recover from is posted as one instead (<see cref="TakeDeviceLoss"/>).
    /// </summary>
    private void Fail(Exception e)
    {
        if (TakeDeviceLoss(e))
        {
            return;
        }

        lock (_gate)
        {
            _failure ??= e;
            RequestStop("failure: " + e.Message);
        }

        _events.Failed(e);
    }

    /// <summary>Stops taking frames; the first reason is kept. Called under the gate.</summary>
    private void RequestStop(string reason)
    {
        if (_stopped)
        {
            return;
        }

        _stopReason = reason;
        _stopped = true;
    }

    /// <summary>Microseconds since the anchor at <paramref name="qpc"/>.</summary>
    private long MicrosAt(long qpc) => (long)((Int128)(qpc - _anchorQpc) * 1_000_000 / _qpcFrequency);

    /// <summary>The Buffer Window from the next Reset on (AD-8); the orchestrator calls it just before a Reset.</summary>
    public void SetBufferWindow(int seconds) => _pendingWindow = seconds;

    /// <summary>
    /// At the Reset <paramref name="us"/> (orchestrator thread): moves the current chain's Replay
    /// Buffer to a ring of the Buffer Window <see cref="SetBufferWindow"/> asked for, seeded from
    /// the Reset keyframe on (<see cref="EncoderChain.SnapStart"/>), when that differs from the
    /// ring's. Nothing on a drained session, or one without a ring.
    /// </summary>
    private void ResizeAt(long us)
    {
        var pending = _pendingWindow;
        EncoderChain? chain;
        lock (_chainLock)
        {
            // An armed chain takes over at the next frame: resizing the outgoing one would be lost,
            // so the incoming one takes the window at the next Reset instead.
            // A forced restart moves the ring to a generation of its own; the window applies at the next
            // Reset. So does a recovery from a lost device.
            chain = _armed is null && !_restarting && !_deviceLost ? _current : null;
        }

        if (_keepRing || _outputFinished || chain?.Ring is not { } ring || pending < 1 || pending == chain.WindowSeconds)
        {
            return;
        }

        var next = chain.NewRing(pending);
        int generation;
        try
        {
            generation = ring.Resize(next, chain.SnapStart(us));
        }
        catch
        {
            next.Dispose();
            throw;
        }

        _log.LogInformation(
            "Buffer Window {Old} s -> {New} s at the Reset {Reset:0.000} s after the anchor: ring generation {Generation} {Storage}, {Capacity} packets preallocated",
            chain.WindowSeconds, pending, us / 1e6, generation, EncoderChain.StorageOf(next), chain.RingCapacity(pending));
        chain.WindowSeconds = pending;
    }

    /// <summary>Moves the Reset forward to <paramref name="us"/> (µs since the anchor); an earlier one is ignored.</summary>
    private void ResetTo(long us)
    {
        var current = Volatile.Read(ref _resetUs);
        while (us > current)
        {
            var seen = Interlocked.CompareExchange(ref _resetUs, us, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    /// <summary>Frees the replaced chain, if any, the current one (a drained session keeps its ring until Release) and the device.</summary>
    private void DisposeAll()
    {
        EncoderChain? retiring;
        EncoderChain? current;
        lock (_chainLock)
        {
            retiring = _retiring;
            current = _current;
        }

        retiring?.Dispose();

        // A save still writing holds its own lease and pins; the ring goes when the last is released.
        // A chain a GPU reset tore down kept its ring: it goes here unless drained.
        if (current is { IsDisposed: true } && !_keepRing)
        {
            current.Ring?.Dispose();
        }

        current?.Dispose(keepRing: _keepRing);
        _device?.Dispose();
    }

    /// <summary>
    /// One save's write (AD-8), bound to one encoder chain: its sink and encoder throughout, so it
    /// never mixes two chains' PTS. It leases the ring on the orchestrator thread, so ending the
    /// session can't free it, and registers on the chain, so a switch can't tear it down before the
    /// cut; it then waits, cuts (pinning the slabs and releasing the lease) and writes on a
    /// background thread of its own, which never blocks the ring, and ends its registration once.
    /// <see cref="Write"/> requests the keyframe after the press before that thread starts, and may
    /// arm the switch to a prepared chain (<see cref="SwitchesTo"/>); with no
    /// <paramref name="video"/> (a drained session) it asks for none, and the Clip ends at the press.
    /// </summary>
    private sealed class ClipWrite(CaptureSession session, EncoderChain chain, VideoEncoder? video, long qpc, int windowSeconds) : IClipWrite
    {
        private EncoderChain _chain = chain;
        private VideoEncoder? _video = video;
        private int _windowSeconds = windowSeconds;

        // Where the written Clip ends, in µs since the anchor; set before done is called.
        private long _endUs = long.MinValue;

        // The keyframe request's ticket, taken in Write before the writer thread starts.
        private long _ticket;

        public SessionSpec? SwitchesTo { get; private set; }

        /// <summary>The session's next Clip starts at this one's end: the keyframe it stopped before.</summary>
        public void Reset()
        {
            var end = Volatile.Read(ref _endUs);
            if (end != long.MinValue)
            {
                session.ResetTo(end);
                session.ResizeAt(end);
            }
        }

        public void Write(string path, Action<Exception?> done)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(done);
            lock (session._chainLock)
            {
                // Replaced and torn down since Save (an earlier press's switch took a frame in
                // between): the press cuts from the chain that replaced it.
                if (_chain.RetireStarted && session._current is { } current && !ReferenceEquals(current, _chain))
                {
                    _chain = current;
                    _video = session._outputFinished ? null : current.Video;
                    _windowSeconds = current.WindowSeconds;
                }

                _chain.Writers++;
            }

            var bound = _chain;
            var ring = bound.Ring!;
            if (!ring.Lease())
            {
                session.EndWriter(bound, null);
                throw new InvalidOperationException("The Replay Buffer has already been released.");
            }

            try
            {
                // A switch is armed first: then its keyframe is asked for after the arm, the instant the
                // new chain takes frames from, so the old Clip ends on the new chain's first frame. Frames
                // captured between the press and the arm reach only the old chain, and stay in its Clip.
                var armed = session.TryArm(bound, qpc, out var armQpc);
                SwitchesTo = armed?.Spec;

                // Only a write that starts asks for its keyframe; starting the thread publishes the ticket to it.
                if (_video is not null)
                {
                    _ticket = _video.RequestKeyframe(armed is null ? qpc : armQpc);
                }

                new Thread(() => done(Run(path))) { Name = "ClipWriter", IsBackground = true }.Start();
            }
            catch
            {
                ring.Release();
                session.EndWriter(bound, SwitchesTo is null ? null : session.MicrosAt(qpc));
                throw;
            }
        }

        private Exception? Run(string path)
        {
            var log = session._log;
            var chain = _chain;
            var ring = chain.Ring!;
            var leased = true;
            var registered = true;
            long? claimed = null;
            long? end = null;

            // The chain may retire once its last save has cut; the switching save first records where its Clip ends.
            void EndRegistration()
            {
                registered = false;
                session.EndWriter(chain, SwitchesTo is null ? null : end ?? session.MicrosAt(qpc));
            }

            try
            {
                var pressUs = session.MicrosAt(qpc);
                var toUs = WaitForKey(ring, pressUs);
                end = toUs;

                // Claimed as soon as the end is known: a later write starts here, not back over this one.
                var previous = session._claim.Claim(toUs);
                claimed = toUs;

                // Every packet whose centre is before the key has been appended once each stream is an AAC frame past it.
                var deadline = Deadline();
                while (!ring.AllStreamsPast(toUs + AacFrameUs) && !session._outputFinished && Stopwatch.GetTimestamp() < deadline)
                {
                    Thread.Sleep(10);
                }

                var windowUs = _windowSeconds * 1_000_000L;
                // The window back from the Clip's end, the key after the press, so the Clip never exceeds it.
                var ownFromUs = Math.Max(toUs - windowUs, Volatile.Read(ref session._resetUs));
                var fromUs = Math.Max(ownFromUs, previous);

                // R4: an empty cut, whatever set its start, makes no Clip.
                if (toUs <= fromUs)
                {
                    throw new ErrorCodeException(ErrorCodes.ClipTooShort, "The cut is empty: its start is at or after its end.");
                }

                // R4: an earlier write still in flight has claimed all but less than 1 s of the span. Strictly
                // past the Reset: a succeeded claim equals the Reset once it lands, and the orchestrator gates that case.
                if (previous > ownFromUs && toUs - fromUs < 1_000_000)
                {
                    throw new ErrorCodeException(ErrorCodes.ClipTooShort, "An earlier Clip still being written leaves less than 1 s to save.");
                }

                using var source = ring.Cut(chain.SnapStart(fromUs), toUs);
                ring.Release();
                leased = false;

                // The cut holds its own pins and parameters: the chain's encoders are no longer needed.
                EndRegistration();
                if (source is null)
                {
                    throw new ErrorCodeException(
                        ErrorCodes.ClipTooShort,
                        "There's nothing to save yet: ReplayBuffer has kept less than 1 s of gameplay since the game started or the last save.");
                }

                if (session._options.ClipWriteFault?.Invoke(path) is { } fault)
                {
                    throw fault;
                }

                ClipWriter.Write(source, path, log);

                // The cut's end: the press's keyframe, or a ring generation's boundary when the cut came from the one before it.
                var endUs = source.Cut.EndUs;
                session._claim.Succeeded(toUs, endUs);
                claimed = null;
                Volatile.Write(ref _endUs, endUs);
                log.LogInformation(
                    "Clip of {Seconds:0.000} s, {Start:0.000} s to {End:0.000} s after the anchor ({Packets} packets), written to {Path}",
                    (source.Cut.EndUs - source.Cut.StartUs) / 1e6, source.Cut.StartUs / 1e6, source.Cut.EndUs / 1e6, source.Cut.Packets.Count, path);
                return null;
            }
            catch (Exception e)
            {
                // Give the span back: a later write starts at the last end still claimed.
                if (claimed is { } toUs)
                {
                    session._claim.Failed(toUs);
                }

                // No file is left behind: neither the part nor the reserved one.
                foreach (var file in new[] { path + ClipWriter.PartExtension, path })
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception deleteFailure) when (deleteFailure is IOException or UnauthorizedAccessException)
                    {
                        log.LogWarning("Could not delete {Path}: {Message}", file, deleteFailure.Message);
                    }
                }

                return e;
            }
            finally
            {
                if (leased)
                {
                    ring.Release();
                }

                if (registered)
                {
                    EndRegistration();
                }
            }
        }

        /// <summary>
        /// The forced keyframe's start in µs since the anchor, once the encoder has taken a frame
        /// after the request; the press itself when that takes longer than <see cref="SaveWait"/> or
        /// the output has finished first.
        /// </summary>
        private long WaitForKey(ReplayRingSink ring, long pressUs)
        {
            if (_video is not { } video)
            {
                // Drained: the ring already holds everything it will.
                return pressUs;
            }

            var deadline = Deadline();
            while (true)
            {
                if (video.TryGetForcedKey(_ticket, out var pts))
                {
                    return ring.VideoMicros(pts);
                }

                if (session._outputFinished || Stopwatch.GetTimestamp() >= deadline)
                {
                    session._log.LogInformation("No keyframe was encoded after the press within {Seconds:0.0} s; the Clip ends at the press", SaveWait.TotalSeconds);
                    return pressUs;
                }

                Thread.Sleep(5);
            }
        }

        private long Deadline() => Stopwatch.GetTimestamp() + (long)(SaveWait.TotalSeconds * session._qpcFrequency);
    }

    /// <summary>
    /// One Recording (R2): <see cref="Start"/> attaches a <see cref="RecordingWriter"/> with its own
    /// copies of the streams to the tap, then asks the encoder for a keyframe after the press; the
    /// writer's thread does the rest. <see cref="Stop"/> hands it the press as µs since the anchor.
    /// </summary>
    private sealed class RecordingWrite(CaptureSession session, EncoderChain chain, RecordingTap tap, VideoEncoder video, long qpc) : IRecordingWrite
    {
        private RecordingWriter? _writer;

        public void Start(string path, Action<Exception?> done)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(done);
            if (_writer is not null)
            {
                throw new InvalidOperationException("The Recording has already started.");
            }

            // Set before the writer thread starts, which publishes it to the lambda.
            long ticket = 0;
            var streams = tap.CopyStreams();
            RecordingWriter writer;
            try
            {
                writer = new RecordingWriter(
                    path,
                    streams,
                    () => video.TryGetForcedKey(ticket, out var pts) ? pts : null,
                    session.MicrosAt(qpc),
                    session._loggers.CreateLogger<RecordingWriter>(),
                    done);
            }
            catch
            {
                streams.Dispose();
                throw;
            }

            try
            {
                // Story 12.2: attached under the chain lock, and only to the chain Record reserved
                // while it is still current, so a size move can't switch it out from under the
                // Recording; the reservation ends here either way.
                lock (session._chainLock)
                {
                    session._recordReservedUntil = 0;
                    if (!ReferenceEquals(session._current, chain) || chain.Retiring || chain.IsDisposed)
                    {
                        throw new InvalidOperationException("The game's video changed size just as the Recording started; press Record again.");
                    }

                    // Attached before the keyframe is asked for, so the forced key can't pass the tap unseen.
                    tap.Attach(writer);
                }

                ticket = video.RequestKeyframe(qpc);
                writer.Start();
            }
            catch
            {
                writer.Abandon();
                throw;
            }

            _writer = writer;
            session._log.LogInformation("Recording to {Path}", path);
        }

        public void Stop(long qpc) => _writer?.Stop(session.MicrosAt(qpc));
    }
}
