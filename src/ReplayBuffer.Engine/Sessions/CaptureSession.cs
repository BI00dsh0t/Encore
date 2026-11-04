using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Core.Audio;
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

    /// <summary>With no first frame by then, the session posts a <see cref="ErrorCodes.CaptureFailed"/> failure.</summary>
    public TimeSpan FirstFrameTimeout { get; init; } = DefaultFirstFrameTimeout;

    /// <summary>
    /// Called on a background thread with the message when a switch to a profile's new encoder
    /// chain left a gap of more than one frame before the next Clip (<see cref="ErrorCodes.ResetGap"/>);
    /// null ignores it.
    /// </summary>
    public Action<string>? ResetGap { get; init; }
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
/// the bitrate and the rate control. The capture thread only scales each frame into the fixed output,
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
/// (<see cref="ISessionEvents.ChainRestarted"/>), and a failed rebuild fails the session.
/// </summary>
internal sealed class CaptureSession : ICaptureSession
{
    /// <summary>One AAC frame (1024 samples at 48 kHz) in µs, rounded up.</summary>
    private const long AacFrameUs = ((1024 * 1_000_000L) + AudioTimeline.SampleRate - 1) / AudioTimeline.SampleRate;

    /// <summary>How long a save waits for every stream to pass the press before it cuts.</summary>
    private static readonly TimeSpan SaveWait = TimeSpan.FromSeconds(1);

    private readonly SessionSpec _spec;
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
    private string? _path;
    private D3D11Device? _device;
    private WindowCapture? _capture;
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
        // Before anything is opened: the first codec use may run the probe.
        var (encoder, codec) = ResolveEncoder(_spec);
        var target = _spec.Target;
        _window = WindowFinder.FromHandle(target.Window);
        if (!_options.Replay)
        {
            _path = _options.OutputPath is { } explicitPath
                ? Path.GetFullPath(explicitPath)
                : OutputNaming.Allocate(_spec.Snapshot.OutputFolder ?? _options.OutputFolder, _window?.Title ?? "Capture", DateTime.Now, File.Exists);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        }

        _device = D3D11Device.Create(target.Window, _loggers.CreateLogger<D3D11Device>());
        if (_options.GpuPriority is { } priority)
        {
            SetGpuPriority(priority);
        }

        // The first chain: its taps hold the last second of blocks until the anchor starts its audio.
        var first = new EncoderChain(_spec, encoder, codec, _path, _options, _device, _audio, _micMute, _loggers, _log, Fail);
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

        var capture = NewCapture(target.Window);
        lock (_gate)
        {
            _capture = capture;
        }

        _firstFrameTimer.Change(_options.FirstFrameTimeout, Timeout.InfiniteTimeSpan);
        capture.Start();
    }

    public void Retarget(CaptureTarget target)
    {
        // The caller's pid is not trusted: the window itself must belong to the session's process.
        var window = WindowFinder.FromHandle(target.Window);
        if (window is null || (int)window.ProcessId != target.ProcessId)
        {
            throw new ErrorCodeException(
                ErrorCodes.RetargetOtherProcess,
                $"Window 0x{target.Window:X} does not belong to process {target.ProcessId}.");
        }

        WindowCapture? old;
        lock (_gate)
        {
            if (_stopped)
            {
                throw new ErrorCodeException(ErrorCodes.SessionNotRunning, "The session has already stopped.");
            }

            old = _capture;
            _capture = null;
        }

        // Disposing waits for a frame in flight, so the two captures never overlap frames.
        old?.Dispose();
        var next = NewCapture(target.Window);
        lock (_gate)
        {
            if (_stopped)
            {
                next.Dispose();
                return;
            }

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
        WindowCapture? capture;

        // A first frame still opening the encoders holds the gate; let it finish.
        lock (_gate)
        {
            RequestStop(reason);
            anchored = _anchored;
            capture = _capture;
            _capture = null;
        }

        _firstFrameTimer.Dispose();

        // A forced restart running finishes first: the chain it installs (or the one it failed on) is current.
        Thread? restart;
        lock (_chainLock)
        {
            restart = _restartThread;
        }

        restart?.Join();
        EncoderChain? chain;
        lock (_chainLock)
        {
            chain = _current;
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

            // A failed restart left nothing to flush: its chain is gone, and the session has failed.
            if (!chain!.IsDisposed)
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
        }

        if (_failure is null)
        {
            var seconds = (double)(_lastFrameQpc - _anchorQpc) / _qpcFrequency;
            if (_path is null && _keepRing)
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
        _keepRing = _options.Replay;
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
        if (!_options.Replay || _released || !Volatile.Read(ref _anchored))
        {
            return null;
        }

        EncoderChain? chain;
        lock (_chainLock)
        {
            // A forced restart is rebuilding the chain: nothing to cut from until it is done.
            chain = _restarting ? null : _current;
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
    /// In Replay mode, once the output is open and while it runs: a Recording from
    /// <paramref name="qpc"/> on the current chain's encoders, through its tap; null once drained or
    /// released, before the anchor, while a Recording is attached, while a chain switch is armed, or
    /// while a forced restart rebuilds the chain.
    /// </summary>
    public IRecordingWrite? Record(long qpc)
    {
        // The anchor's release write publishes the tap and the encoder.
        if (!_options.Replay || _released || _outputFinished || !Volatile.Read(ref _anchored))
        {
            return null;
        }

        EncoderChain? chain;
        lock (_chainLock)
        {
            if (_armed is not null || _restarting)
            {
                return null;
            }

            chain = _current;
        }

        if (chain is null || chain.IsDisposed || chain.Tap is not { HasWriter: false } tap || chain.Video is not { } video)
        {
            return null;
        }

        return new RecordingWrite(this, tap, video, qpc);
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
        if (!_options.Replay)
        {
            return;
        }

        lock (_chainLock)
        {
            if (_chainsClosed || _keepRing || _outputFinished || Volatile.Read(ref _stopped))
            {
                return;
            }

            var current = (_armed ?? _current)?.Spec ?? _spec;
            EncoderChain? drop;
            if (next is null || ChainKey.SameChain(next, current))
            {
                drop = _prepared;
                _prepared = null;
                _wanted = null;
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
                if (!_options.Replay || !Volatile.Read(ref _anchored) || _stopped || _outputFinished || _keepRing || _chainsClosed
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

    /// <summary><see cref="RestartChain"/>'s work, on its own thread.</summary>
    private void Restart(EncoderChain old, string reason)
    {
        var began = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_stopped)
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

            old.Dispose(keepRing: true);
            if (_options.RestartFault?.Invoke() is { } fault)
            {
                throw fault;
            }

            next = new EncoderChain(old.Spec, old.EncoderName, old.Codec, null, _options, _device!, _audio, _micMute, _loggers, _log, Fail);
            next.Subscribe();
            next.Build(_anchorQpc, first: false, latency: null, restartOf: old.Ring, window: old.WindowSeconds);
            var now = Stopwatch.GetTimestamp();
            next.StartAudio(now);
            lock (_gate)
            {
                lock (_chainLock)
                {
                    _current = next;
                    installed = true;
                    _restarting = false;
                    StartBuildLocked();
                }
            }

            _log.LogInformation("The encoder chain restarted in {Ms:0} ms; the frames captured meanwhile were dropped", (now - began) * 1000.0 / _qpcFrequency);
            _events.ChainRestarted(now);
        }
        catch (Exception e)
        {
            // Not yet current: the new chain and the ring go with the session, which fails here.
            if (!installed)
            {
                next?.Dispose();
                old.Ring?.Dispose();
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

    /// <summary>Frees what <see cref="Open"/> managed to open, when it threw.</summary>
    public void Abandon()
    {
        WindowCapture? capture;
        lock (_gate)
        {
            _stopped = true;
            capture = _capture;
            _capture = null;
        }

        _firstFrameTimer.Dispose();
        capture?.Dispose();
        DisposeAll();
    }

    internal static string CodeOf(Exception e) => e switch
    {
        EngineFailure f => f.Code,
        ErrorCodeException c => c.Code,
        _ => ErrorCodes.EncodeFailed,
    };

    private WindowCapture NewCapture(nint window)
    {
        int fps;
        lock (_chainLock)
        {
            fps = _current?.Fps ?? _spec.Snapshot.Video.Fps;
        }

        var capture = new WindowCapture(_device!, window, fps, OnFrame, _loggers.CreateLogger<WindowCapture>());
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

            _stats?.Cadence.Record(qpc);
            _lastFrameQpc = qpc;
            if (_options.Stages == CaptureStages.Wgc)
            {
                return;
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
        _log.LogInformation("Anchor QPC {Anchor}; output {Size}, content {Content}", anchorQpc, chain.OutputSize, contentSize);

        _stats?.SnapshotAnchor();
        chain.Build(anchorQpc, first: true, _stats?.Latency);

        // The hub's streams have run since Open; each device takes blocks from here, its held ones
        // replayed first, cut at the anchor once, and fanned out to every Track that holds it.
        chain.StartAudio(anchorQpc);

        // A release write: SetMicMuted, Save and Record read the anchor without the gate.
        Volatile.Write(ref _anchored, true);

        // A chain asked for before the anchor is built from here.
        lock (_chainLock)
        {
            StartBuildLocked();
        }

        _options.Anchored?.Invoke(anchorQpc);
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
                if (_prepared is { } prepared && _armed is null && _retiring is null && !_restarting && ReferenceEquals(chain, _current)
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
            ? $"The Replay Buffer's new encoder chain had no frame {(MicrosAt(Stopwatch.GetTimestamp()) - end) / 1000} ms after the last Clip's end; the next Clip starts at its first frame."
            : $"The Replay Buffer's new encoder chain started {(first - end) / 1000} ms after the last Clip's end; the next Clip starts that much later.";
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
        if (_wanted is not { } spec || _chainsClosed || !Volatile.Read(ref _anchored) || _building || _restarting
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
            chain = new EncoderChain(spec, encoder, codec, null, _options, _device!, _audio, _micMute, _loggers, _log, Fail);
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
            if (chain is not null && !_chainsClosed && !_outputFinished && !Volatile.Read(ref _stopped)
                && _wanted is { } wanted && ChainKey.SameChain(wanted, spec))
            {
                _prepared = chain;
            }
            else if (chain is not null)
            {
                // Superseded meanwhile: torn down before the newer spec's build starts.
                TearDownLocked(chain);
            }
            else if (ReferenceEquals(_wanted, spec))
            {
                _wanted = null;
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

    private void OnClosed(WindowCapture capture)
    {
        lock (_gate)
        {
            // A window left by a retarget, or a session already stopping, doesn't end anything.
            if (_stopped || !ReferenceEquals(capture, _capture))
            {
                return;
            }

            RequestStop("window closed");
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

    /// <summary>Records the first failure and stops; the orchestrator is told, and ends the session.</summary>
    private void Fail(Exception e)
    {
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
            // A forced restart moves the ring to a generation of its own; the window applies at the next Reset.
            chain = _armed is null && !_restarting ? _current : null;
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
                        "The Replay Buffer holds no keyframe before the cut yet, or less than 1 s since a ring generation began; there is nothing to save.");
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
    private sealed class RecordingWrite(CaptureSession session, RecordingTap tap, VideoEncoder video, long qpc) : IRecordingWrite
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
                // Attached before the keyframe is asked for, so the forced key can't pass the tap unseen.
                tap.Attach(writer);
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
