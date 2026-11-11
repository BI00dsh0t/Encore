using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Audio;
using Encore.Core.Output;
using Encore.Core.Sessions;
using Encore.Media;
using Encore.Platform.Audio;
using Encore.Platform.Capture;
using Encore.Platform.FFmpeg;
using Encore.Platform.Windows;
using SessionFlow = Encore.Contracts.Settings.AudioFlow;
using VideoCodec = Encore.Contracts.Settings.VideoCodec;

namespace Encore.Engine.Sessions;

/// <summary>One capture session: capture, scaler, encoders and Tracks, into a file or Rewind's ring. docs/code-notes.md.</summary>
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

    // The game's captured size at the anchor, then the size Rewind last moved to; for chains that match the source.
    private VideoSize? _sourceSize;

    // The held size change: the watch runs on the capture thread under the gate; a move's state is under the chain lock.
    private readonly SourceSizeWatch _sizeWatch = new(Stopwatch.Frequency);
    private VideoSize? _movingTo;
    private bool _armMove;
    private bool _armPending;
    private volatile bool _buildAhead = true;

    // A Record press reserves the current chain until its writer attaches, so no size move starts in between.
    private long _recordReservedUntil;
    private static readonly TimeSpan RecordReserve = TimeSpan.FromSeconds(10);
    private string? _path;
    private D3D11Device? _device;
    private ICaptureSource? _capture;
    private volatile bool _outputFinished;

    // The exit drain, orchestrator thread only: the teardown keeps the ring for saves until Release.
    private bool _keepRing;
    private bool _released;

    // Rewind's last Reset in µs since the anchor: the end of the last Clip. It only moves forward.
    private long _resetUs = long.MinValue;

    // How far this session's Clips have claimed the ring, so two concurrent Clips never overlap.
    private readonly ClipClaim _claim = new();

    // The window SetBufferWindow asked for, applied at the next Reset; 0 for none.
    private volatile int _pendingWindow;

    // The encoder chains. _chainLock guards them and is only ever taken after _gate. See docs/code-notes.md.
    private readonly Lock _chainLock = new();
    private readonly List<Thread> _workers = [];
    private EncoderChain? _current;
    private EncoderChain? _armed;
    private long _armedQpc;
    private EncoderChain? _retiring;
    private EncoderChain? _prepared;
    private SessionSpec? _wanted;
    private bool _building;

    // Chains being torn down after a newer spec replaced them; a build waits for them, to stay within two chains.
    private int _tearingDown;
    private volatile bool _chainsClosed;

    // A forced restart, under the chain lock: whether one runs, and its thread, which End joins.
    private bool _restarting;
    private Thread? _restartThread;

    // A GPU device loss, under the chain lock; _deviceLost is also written under the gate for the capture thread.
    private bool _deviceLost;
    private bool _recovering;

    // A sleep, from Suspend until the resume's RecoverDevice; an attempt still running then installs nothing.
    private bool _suspended;
    private bool _tornDown;
    private RecordingTap? _lostTap;
    private SessionSpec? _rebuildSpec;
    private Thread? _recoveryThread;

    // Checks the device from the anchor on; under the gate.
    private Timer? _deviceWatch;

    private readonly EncoderResolver _encoderResolver;
    private readonly CaptureSources _sources;
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
        _loggers = loggers;
        _log = loggers.CreateLogger<CaptureSession>();
        _encoderResolver = new EncoderResolver(options.Encoder, encoders, _log);
        _sources = new CaptureSources(options, loggers, _log, OnFrame, OnClosed, Fail);
        _stats = options.Stats;
        _micMute = new MicMuteSchedule(spec.MicMuted);
        _firstFrameTimer = new Timer(_ => OnFirstFrameTimeout(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Opens the device, subscribes to the audio devices and starts capture; the output opens on the first frame.</summary>
    public void Open()
    {
        // Several screens, or one screen matching its source: the plan sets codec and size before the encoder resolves.
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
                        (_spec, _) = CanvasPlanner.Plan(_spec, [one], placements, _encoderResolver.CanEncode, _options.Warn, _log);
                    }
                }
            }
        }

        // Before anything is opened: the first codec use may run the probe.
        var (encoder, codec) = _encoderResolver.Resolve(_spec);
        var target = _spec.Target;
        if (_composite is not null)
        {
            _monitor = _composite[0].Monitor;
            _log.LogInformation("Monitor capture of {Count} screens composited: {Screens}", _composite.Count, string.Join("; ", _composite));
        }
        else if (IsMonitor)
        {
            // One screen, captured directly: the resolved one, else the older single-monitor setting's (null is the primary).
            _monitor = MonitorFinder.Find(_spec.Screens is [var only] ? only.DeviceName : _spec.Snapshot.MonitorRecordingMonitor, _log);
            _log.LogInformation("Monitor capture of {Device}", _monitor.DeviceName);
        }
        else if (_options.CompositeMonitors is { } names)
        {
            // The dev-only composite: a file session of the monitors, on the primary composited monitor's adapter.
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
            _sources.StartForMonitor(_composite, _monitor, _device, first.Fps, StartInstalled);
            return;
        }

        ICaptureSource capture = _composite is not null
            ? _sources.Composite(_composite, _device, first.Fps)
            : _sources.ForWindow(target.Window, _device, first.Fps);
        lock (_gate)
        {
            _capture = capture;
        }

        _firstFrameTimer.Change(_options.FirstFrameTimeout, Timeout.InfiniteTimeSpan);
        capture.Start();
    }

    /// <summary>For tests: which API the session captures through, <c>wgc</c> or <c>desktop-duplication</c>.</summary>
    internal string CaptureApi => _sources.Api;

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

    /// <summary>Plans the canvas of the screens; its codec and size replace the snapshot's video.</summary>
    private void PlanCanvas(IReadOnlyList<DisplayMonitor> screens)
    {
        (_spec, _composite) = CanvasPlanner.Plan(
            _spec, screens, MonitorCanvas.Place(screens), _encoderResolver.CanEncode, _options.Warn, _log);
    }

    /// <summary>Makes every chain of the session, at the snapshot's size or, when matching the source, the source's size.</summary>
    private EncoderChain NewChain(SessionSpec spec, string encoder, VideoCodec codec, string? path, D3D11Device device) =>
        SizedToSource(new EncoderChain(spec, encoder, codec, path, _options, device, _audio, _micMute, _loggers, _log, Fail));

    /// <summary>Sizes an unbuilt game chain from the game's captured size when the snapshot has <c>matchSource</c> on.</summary>
    private EncoderChain SizedToSource(EncoderChain chain) => chain.SizedToSource(IsMonitor, _movingTo ?? _sourceSize);

    /// <summary>Whether a size move may build its chain ahead (false while a Monitor Recording holds the other chain).</summary>
    public void SetBuildAhead(bool allowed) => _buildAhead = allowed;

    /// <summary>Watches one frame's size, under the gate: a size that held 3 s starts a move, unless a Recording is attached.</summary>
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

    /// <summary>Moves Rewind to a new size, under the gate; refused while anything else is changing. See docs/code-notes.md.</summary>
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
                _log.LogInformation("The game's size changed to {Size} and held 3 s: Rewind moves to it (built ahead)", target);
                _wanted = old.Spec;
                _armMove = true;
                StartBuildLocked();
            }
            else
            {
                _log.LogInformation("The game's size changed to {Size} and held 3 s: Rewind moves to it, torn down first while a Monitor Recording runs", target);
                _wanted = null;
                _restarting = true;
                _restartThread = new Thread(() => Restart(old, "the game's size changed while a Monitor Recording runs", old.Spec)) { Name = "Encoder chain move", IsBackground = true };
                _restartThread.Start();
            }
        }
    }

    /// <summary>Queues <see cref="ArmMove"/> when a move's chain is prepared and nothing blocks it. Under the chain lock.</summary>
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

    /// <summary>Arms a size move's prepared chain with no save, on a background thread; never while a Recording is attached.</summary>
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
        _log.LogInformation("The size move switches Rewind to its new encoder chain {At:0.000} s after the anchor", MicrosAt(armQpc) / 1e6);
    }

    /// <summary>A move ends once a chain at its size is current: that size is the source's from here. Under the chain lock.</summary>
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

    /// <summary>For tests: whether a size move is pending (building or waiting to arm), so a Record press now cancels it.</summary>
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

    /// <summary>For tests: how many size moves a Recording's start cancelled. Under the chain lock.</summary>
    internal int MovesCancelled { get; private set; }

    /// <summary>Whether a Record press holds the current chain until its writer attaches. Under the chain lock.</summary>
    private bool RecordReservedLocked() => Stopwatch.GetTimestamp() < _recordReservedUntil;

    /// <summary>A Recording starts on the current chain, so a pending size move is cancelled. Under the chain lock.</summary>
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

    /// <summary>Forgets a pending move, so the size must hold again. Under the chain lock.</summary>
    private void DropMoveLocked()
    {
        _movingTo = null;
        _armMove = false;
    }

    /// <summary>Whether this is the Monitor session, not a game window's.</summary>
    private bool IsMonitor => _spec.Kind == SessionKind.Monitor;

    /// <inheritdoc/>
    public string? MonitorDevice => _monitor is { DeviceName.Length: > 0 } monitor ? monitor.DeviceName : null;

    /// <summary>The screens still composited, else <see cref="MonitorDevice"/> alone.</summary>
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

    /// <summary>Stops capturing a removed composited screen; its area turns black. Outside the gate.</summary>
    public bool DropMonitor(string device)
    {
        ICaptureSource? capture;
        lock (_gate)
        {
            capture = _capture;
        }

        return capture is CompositeCapture composite && composite.Drop(device);
    }

    /// <summary>Whether it holds a Rewind: Replay mode, and not the Monitor session.</summary>
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

            // Built under the gate, so a recovery's teardown can't dispose the device under this capture.
            next = _sources.ForWindow(target.Window, device, CaptureFps());
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

        // A forced restart or recovery that is running finishes first; a recovery sees the stop and installs nothing.
        Thread? restart;
        Thread? recovery;
        lock (_chainLock)
        {
            restart = _restartThread;
            recovery = _recoveryThread;
        }

        restart?.Join();

        // Bounded: an attempt hung on the driver must not freeze the orchestrator.
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
                return new SessionOutcome(null, _stopReason, new EngineFailure(_failure is null ? ErrorCodes.CaptureFailed : EngineFailure.CodeOf(_failure), message));
            }

            // A failed restart left nothing to flush, and a lost device's chain can't be flushed.
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
                _log.LogInformation("Draining: buffered {Seconds:0.0} s; Rewind is kept for saves until the drain is released", seconds);
            }
            else if (_path is null)
            {
                _log.LogInformation("Buffered {Seconds:0.0} s; Rewind is released once no save holds it", seconds);
            }
            else
            {
                _log.LogInformation("Wrote {Seconds:0.0} s to {Path}", seconds, _path);
            }
        }

        return new SessionOutcome(_path, _stopReason, _failure);
    }

    /// <summary>Like <see cref="End"/>, but in Replay mode the ring is kept, so a save still cuts from it until Release.</summary>
    public SessionOutcome Drain(string reason)
    {
        _keepRing = HasRing;
        return End(reason);
    }

    /// <summary>Frees the ring <see cref="Drain"/> kept, once; a save still writing keeps its own lease.</summary>
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
            _log.LogInformation("The drained Rewind is released once no save holds it");
        }
    }

    /// <summary>Starts a Clip write up to a QPC time, bound to the current chain; null when refused. See docs/code-notes.md.</summary>
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
            // A forced restart leaves nothing to cut until it is done; a lost device's chain is cut as it is.
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
                throw new ErrorCodeException(ErrorCodes.ClipTooShort, "Rewind holds less than 1 s; there is nothing to save yet.");
            }

            // No encoder runs to ask for a keyframe: the Clip ends at the ring's last packet before the press.
            return new ClipWrite(this, chain, null, qpc, chain.WindowSeconds);
        }

        // A chain disposed while the output still runs is one a failed restart left; a drained one keeps its ring.
        if (chain is null || (chain.IsDisposed && !_outputFinished) || chain.Ring is null || chain.Video is not { } video)
        {
            return null;
        }

        if (qpc - _anchorQpc < _qpcFrequency)
        {
            throw new ErrorCodeException(ErrorCodes.ClipTooShort, "Rewind holds less than 1 s; there is nothing to save yet.");
        }

        // A drained session's encoder is already disposed: never ask it for a keyframe.
        return new ClipWrite(this, chain, _outputFinished ? null : video, qpc, chain.WindowSeconds);
    }

    /// <summary>Starts a Recording from a QPC time on the current chain; null when the session can't record now.</summary>
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

            // The Recording keeps this chain: a pending size move is cancelled and the chain reserved for its writer.
            CancelMoveLocked();
            _recordReservedUntil = Stopwatch.GetTimestamp() + (long)(RecordReserve.TotalSeconds * _qpcFrequency);
        }

        return new RecordingWrite(this, chain, chain.Tap!, chain.Video!, qpc);
    }

    /// <summary>Builds a spec's chain in advance, on a background thread, for the next save press. See docs/code-notes.md.</summary>
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
                // The same settings leave a size move alone.
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

                // The profile's chain is built at a pending move's size, and switches at a save.
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

    /// <summary>A forced encoder restart, for tests: rebuilds the same spec into a new ring generation. docs/code-notes.md.</summary>
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

    /// <summary>The live encoder chains: current, armed, replaced but still fed, prepared, and one being built.</summary>
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

                // An unwanted build is torn down as soon as it ends; a rebuild counts once the chain it replaces is gone.
                var building = _building && _wanted is not null;
                var rebuilding = _restarting && _current is not { IsDisposed: false };
                return chains + (building ? 1 : 0) + (rebuilding ? 1 : 0);
            }
        }
    }

    /// <summary>Switches to another spec's chain at a Reset, tearing the current one down first. See docs/code-notes.md.</summary>
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

    /// <summary>The restart's work, on its own thread; with switchTo it is a switch: a fresh ring, and the gap is raised.</summary>
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
                var (encoder, codec) = _encoderResolver.Resolve(switchTo);
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
            // The device was lost under the restart: the recovery takes it from here, and the ring is kept.
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

    /// <summary>Starts one attempt to recover from a GPU device loss, on its own thread. Orchestrator thread; never blocks.</summary>
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

    /// <summary>The PC is going to sleep: tears down as for a lost device, so the resume rebuilds. Never blocks.</summary>
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

                // Already lost: a running attempt installs nothing now, and the teardown below runs once it has finished.
                _suspended = true;
                _deviceLost = true;
                DropMoveLocked();
                foreach (var chain in new[] { _current, _armed, _retiring })
                {
                    chain?.Fed = false;
                }

                _log.LogInformation("The PC is going to sleep: tearing capture, the encoder chains and the device down; Rewind's ring is kept");
                var previous = _recoveryThread;
                _recoveryThread = new Thread(() => SuspendTearDown(previous)) { Name = "Suspend teardown", IsBackground = true };
                _recoveryThread.Start();
            }
        }

        return true;
    }

    /// <summary>Suspend's thread: the last attempt and a forced restart finish, then the teardown, then the tap ends.</summary>
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

    /// <summary>One recovery attempt, on its own thread: a new device, chain and capture. Order: docs/code-notes.md.</summary>
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
            var (encoder, codec) = ReferenceEquals(spec, lost.Spec) ? (lost.EncoderName, lost.Codec) : _encoderResolver.Resolve(spec);
            next = NewChain(spec, encoder, codec, null, device);
            next.Subscribe();

            // The same ring in a new generation, so what it held before the reset stays saveable.
            var ring = HasRing && lost.Ring is { IsDisposed: false } kept ? kept : null;
            next.Build(_anchorQpc, first: false, latency: null, restartOf: ring, window: ring is null ? 0 : lost.WindowSeconds);
            if (IsMonitor)
            {
                // WGC, else Desktop Duplication, as at Open; started here and installed below.
                capture = _sources.StartForMonitor(_composite, _monitor, device, next.Fps, c => c.Start());
            }
            else
            {
                capture = _sources.ForWindow(window!.Handle, device, next.Fps);
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

    /// <summary>The first recovery attempt's teardown, in order: WGC, then every chain (the ring kept), then the device.</summary>
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

        _log.LogInformation("The lost GPU device, its capture and its encoder chains were torn down in {Ms:0} ms; Rewind's ring is kept",
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

    /// <summary>The device watchdog's tick: a device that reports itself removed or reset counts as a loss.</summary>
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

    /// <summary>Takes an exception as a GPU device loss when it is one and the session can recover; false leaves it to Fail.</summary>
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

    /// <summary>Whether an exception is a device loss: a removed or reset HRESULT, or a device that says it is lost.</summary>
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

    /// <summary>Makes a capture the session's, then starts it, so a Closed it raises at once isn't taken for an old capture's.</summary>
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

    private int CaptureFps()
    {
        lock (_chainLock)
        {
            return _current?.Fps ?? _spec.Snapshot.Video.Fps;
        }
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

            // The first frame after an armed press: the new chain's first, and where the old chain's forced keyframe lands.
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

        // The hub's streams have run since Open; each device takes blocks from here, cut once at the anchor.
        chain.StartAudio(anchorQpc);

        // A release write: SetMicMuted, Save and Record read the anchor without the gate.
        Volatile.Write(ref _anchored, true);

        // Only a session that can recover watches its device.
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

    /// <summary>Makes the armed chain current at this frame; the old one is fed until its saves have cut. Under the gate.</summary>
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

    /// <summary>Arms the prepared chain at a save press, when one is ready and nothing else is changing. Orchestrator thread.</summary>
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

            // Under the gate no frame is in flight, so every frame captured after this instant reaches the new chain.
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
        _log.LogInformation("The save press {At:0.000} s after the anchor switches Rewind to the prepared encoder chain",
            MicrosAt(qpc) / 1e6);
        return armed;
    }

    /// <summary>Ends a save's registration on a chain once it has cut or failed; a switching save records its Clip's end.</summary>
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

    /// <summary>Starts a chain's retirement once it is replaced and no save is bound to it. Under the chain lock.</summary>
    private void TakeRetirementLocked(EncoderChain chain)
    {
        if (!chain.Retiring || chain.Writers > 0 || chain.RetireStarted || _chainsClosed)
        {
            return;
        }

        chain.RetireStarted = true;
        StartWorkerLocked("Encoder chain retirement", () => Retire(chain));
    }

    /// <summary>On a background thread: tears down the replaced chain, checks for a gap, then starts any build asked for.</summary>
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

    /// <summary>Waits for the next chain's first keyframe; raises <c>reset-gap</c> when it is over a frame after the old Clip.</summary>
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

    /// <summary>After a switch at a Reset: raises <c>reset-gap</c> with the gap from the last Reset to the first keyframe.</summary>
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

    /// <summary>Starts the build of the spec asked for, when nothing is in the way. Under the chain lock.</summary>
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

    /// <summary>On a background thread: builds a spec's chain and hands it over as prepared when it is still wanted.</summary>
    private void Build(SessionSpec spec)
    {
        EncoderChain? chain = null;
        try
        {
            var (encoder, codec) = _encoderResolver.Resolve(spec);
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

                // A move's chain arms at once, without a save.
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

    /// <summary>Tears a chain down on a background thread; no build starts until it is gone. Under the chain lock.</summary>
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

    /// <summary>End's side of the chains: running builds and teardowns are waited for, then prepared and armed chains go.</summary>
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

    /// <summary>Sets the device's GPU thread priority once; a failure is logged and the run goes on.</summary>
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

        // Every device, with the hub's readouts and the samples encoded by the first Track that holds it.
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

    /// <summary>Records the first failure and stops; the orchestrator is told and ends the session.</summary>
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

    /// <summary>The Buffer Window from the next Reset on; the orchestrator calls it just before a Reset.</summary>
    public void SetBufferWindow(int seconds) => _pendingWindow = seconds;

    /// <summary>At a Reset: moves the ring to the Buffer Window that was asked for, seeded from the Reset keyframe on.</summary>
    private void ResizeAt(long us)
    {
        var pending = _pendingWindow;
        EncoderChain? chain;
        lock (_chainLock)
        {
            // The window waits for the next Reset while a chain is armed, a restart runs or the device is lost.
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

    /// <summary>Frees the replaced chain, the current one (a drained session keeps its ring) and the device.</summary>
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

        // A save still writing holds its own lease; a chain a GPU reset tore down gives up its kept ring here.
        if (current is { IsDisposed: true } && !_keepRing)
        {
            current.Ring?.Dispose();
        }

        current?.Dispose(keepRing: _keepRing);
        _device?.Dispose();
    }

    /// <summary>One save's write, bound to one encoder chain so it never mixes two chains' PTS. See docs/code-notes.md.</summary>
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
                // Replaced and torn down since Save: the press cuts from the chain that replaced it.
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
                throw new InvalidOperationException("Rewind has already been released.");
            }

            try
            {
                // A switch is armed first, so the keyframe is asked for at the instant the new chain takes frames from.
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

                // An empty cut, whatever set its start, makes no Clip.
                if (toUs <= fromUs)
                {
                    throw new ErrorCodeException(ErrorCodes.ClipTooShort, "The cut is empty: its start is at or after its end.");
                }

                // An earlier write still in flight has claimed all but under 1 s of the span.
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
                        "There's nothing to save yet: Encore has kept less than 1 s of gameplay since the game started or the last save.");
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

        /// <summary>The forced keyframe's time in µs since the anchor; the press itself when it takes too long.</summary>
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

    /// <summary>One Recording: Start attaches a writer to the tap and asks for a keyframe; the writer's thread does the rest.</summary>
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
                // Attached under the chain lock, and only to the chain Record reserved, so a size move can't switch it out.
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
