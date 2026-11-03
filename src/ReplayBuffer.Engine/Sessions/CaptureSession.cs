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

    /// <summary>With no first frame by then, the session posts a <see cref="ErrorCodes.CaptureFailed"/> failure.</summary>
    public TimeSpan FirstFrameTimeout { get; init; } = DefaultFirstFrameTimeout;
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
/// devices is silence, advanced from QPC every <see cref="SilenceInterval"/> by a timer, off the
/// capture thread; at the end every Track is padded with silence to one common end frame, the
/// longest device's or the last video frame's, whichever is later. The first frame is the anchor, every stream's PTS 0. The snapshot's
/// <c>video</c> sets the encoder (the probe's for its codec, else H.264), the output size, the fps,
/// the bitrate and the rate control. The capture thread only scales each frame into the fixed output,
/// letterboxed (<see cref="FrameScaler"/>), and posts it; the encoder thread emits exactly one frame
/// per slot of the fps, repeating the last one when nothing new arrived (story 1.11,
/// <see cref="PacedVideoEncoder"/>). A retarget swaps only the WGC capture, so the file keeps one
/// unbroken set of streams. Mic mute (AD-13) is one <see cref="MicMuteSchedule"/>, set from the spec
/// before anything opens and then by <see cref="SetMicMuted"/>; each Mic Device's input to each
/// <c>muteAffected</c> Track passes through a <see cref="MicMuteGate"/>, so it is silenced there,
/// before the mix, from the press's frame, and stays audible in every other Track. In Replay mode
/// (<see cref="CaptureSessionOptions.Replay"/>) the streams go into a <see cref="ReplayRingSink"/>
/// instead of a file, and <see cref="Save"/> writes a Clip from it on a thread of its own. The ring
/// is sized from the snapshot's Buffer Window at the anchor; a new window from
/// <see cref="SetBufferWindow"/> resizes it at the next Reset (AD-8), into a new ring generation
/// seeded from the Reset keyframe, so Clips stay gapless.
/// </summary>
internal sealed class CaptureSession : ICaptureSession
{
    /// <summary>AAC packets a second (48 kHz in 1024-sample frames), rounded up, for sizing the ring.</summary>
    private const int AacPacketsPerSecond = 47;

    /// <summary>One AAC frame (1024 samples at 48 kHz) in µs, rounded up.</summary>
    private const long AacFrameUs = ((1024 * 1_000_000L) + AudioTimeline.SampleRate - 1) / AudioTimeline.SampleRate;

    /// <summary>How often a Track with no devices is advanced to the current QPC.</summary>
    private static readonly TimeSpan SilenceInterval = TimeSpan.FromMilliseconds(100);

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
    private readonly List<SessionAudioTrack> _audioTracks = []; // one per device
    private readonly List<AacEncoder> _audioEncoders = []; // one per Track
    private readonly List<TrackMixer> _mixers = []; // one per Track
    private readonly List<AudioTimeline> _audioTimelines = [];
    private readonly MicMuteSchedule _micMute;
    private readonly CaptureStatsCollector? _stats;
    private readonly Timer _firstFrameTimer;
    private readonly Lock _silenceGate = new();
    private Timer? _silenceTimer;
    private bool _silenceStopped;

    private WindowInfo? _window;
    private string? _path;
    private D3D11Device? _device;
    private WindowCapture? _capture;
    private D3D11VAFrames? _frames;
    private FrameScaler? _scaler;
    private PacedVideoEncoder? _paced;
    private FragmentedMp4Muxer? _muxer;
    private ReplayRingSink? _ring;
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

    // The Buffer Window the ring holds, in seconds: set at the anchor, then by a resize at a Reset
    // (orchestrator thread). The window SetBufferWindow asked for, applied at the next Reset; 0 for none.
    private int _windowSeconds;
    private volatile int _pendingWindow;

    // A Disk-mode session's ring folder; null in Memory mode.
    private string? _ringFolder;
    private VideoEncoder? _video;
    private readonly IVideoEncoderCatalog _encoders;
    private readonly VideoSize _outputSize;
    private string _encoderName = "";
    private VideoCodec _codec;
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
        _outputSize = new VideoSize(spec.Snapshot.Video.Width, spec.Snapshot.Video.Height);
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
        (_encoderName, _codec) = ResolveEncoder();
        var target = _spec.Target;
        _window = WindowFinder.FromHandle(target.Window);
        if (_options.Replay)
        {
            if (_spec.Snapshot.ReplayStorage == Contracts.Settings.ReplayStorage.Disk)
            {
                _ringFolder = _spec.Snapshot.RingPath ?? _options.RingFolder;
                if (_ringFolder is null)
                {
                    _log.LogWarning("The Disk-mode Replay Buffer has no ring folder; buffering in memory");
                }
            }
        }
        else
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

        foreach (var device in _spec.Audio.Devices)
        {
            Subscribe(device);
        }

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

            // No frame can arrive now; let the encoder thread offer what is queued before the flush.
            _paced?.Complete();

            // Capture is stopped and no frame is in flight; stop audio, then drain everything.
            // Once a subscription is disposed, its track gets no further block.
            foreach (var track in _audioTracks)
            {
                track.Subscription?.Dispose();
            }

            // Silent Tracks stop with the device taps, not after the teardown below.
            StopSilenceClock();
            ReportAudioFaults();
            Finish();
            WriteStats();
        }
        finally
        {
            // A save still waiting has every packet it will get.
            _outputFinished = true;
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
        if (_ring is { } ring)
        {
            ring.Dispose();
            _log.LogInformation("The drained Replay Buffer is released once no save holds it");
        }
    }

    /// <summary>
    /// In Replay mode, once the output is open: a write of the Clip up to <paramref name="qpc"/>
    /// (AD-10). Its write asks the encoder for a keyframe on the first frame captured after the
    /// press, which is where the Clip ends and, once the orchestrator Resets, where the next one
    /// starts; no keyframe is asked for unless the write starts. Its own thread waits up to
    /// <see cref="SaveWait"/> for that keyframe (else the Clip ends at the press), then up to as long
    /// again for every stream to pass it by an AAC frame (encoding and the AAC FIFO lag it), cuts
    /// <c>[max(key - bufferWindow, last Reset, earlier write's claim), key)</c> from the first
    /// keyframe in it, and writes the Clip. The Buffer Window is the one in effect at the press (a
    /// resize applies at a Reset), and the cut comes from one ring generation: less than 1 s past
    /// a generation's boundary, the previous one up to the boundary, where the Clip then ends and
    /// the next one starts (<see cref="RingGenerations.Cut"/>). The claim (<see cref="ClipClaim"/>) keeps a Clip written
    /// while an earlier one still writes from cutting back over it. A press less than 1 s after the anchor is refused with
    /// <see cref="ErrorCodes.ClipTooShort"/>, before any keyframe is asked for. Once drained
    /// (<see cref="Drain"/>), the encoders are flushed and gone: the write asks for no keyframe, and
    /// the Clip runs to the end of the ring; after <see cref="Release"/> there is nothing to save.
    /// </summary>
    public IClipWrite? Save(long qpc)
    {
        // The anchor's release write publishes the ring and the encoder.
        if (!_options.Replay || _released || !Volatile.Read(ref _anchored) || _ring is not { } ring || _video is not { } video)
        {
            return null;
        }

        if (qpc - _anchorQpc < _qpcFrequency)
        {
            throw new ErrorCodeException(ErrorCodes.ClipTooShort, "The Replay Buffer holds less than 1 s; there is nothing to save yet.");
        }

        // A drained session's encoder is already disposed: never ask it for a keyframe.
        return new ClipWrite(this, ring, _outputFinished ? null : video, qpc, _windowSeconds);
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
        var capture = new WindowCapture(_device!, window, _spec.Snapshot.Video.Fps, OnFrame, _loggers.CreateLogger<WindowCapture>());
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

            if (_video is null)
            {
                StartOutput(qpc, contentSize);
            }

            _stats?.Cadence.Record(qpc);
            _lastFrameQpc = qpc;
            if (_options.Stages == CaptureStages.Wgc)
            {
                return;
            }

            if (_paced is null)
            {
                // --stages copy: the scale alone; submit it, then hand the texture back unencoded.
                if (_video!.TryAcquireFrame(out var drawn))
                {
                    try
                    {
                        _scaler!.Draw(texture, contentSize, drawn.Texture, drawn.Subresource);
                        _device!.Flush();
                    }
                    finally
                    {
                        VideoEncoder.Release(drawn);
                    }
                }

                return;
            }

            // Only draw here; the encoder thread paces, submits and drains. A full hand-off drops the frame.
            if (!_paced.TryReserve() || !_video!.TryAcquireFrame(out var frame))
            {
                return;
            }

            try
            {
                _scaler!.Draw(texture, contentSize, frame.Texture, frame.Subresource);
            }
            catch
            {
                VideoEncoder.Release(frame);
                throw;
            }

            _paced.Post(frame, qpc);
        }
    }

    /// <summary>
    /// The encoder and codec: the forced encoder, else the probe's for the snapshot's codec, else
    /// (a codec this PC can't encode, from a hand-edited or copied document) H.264's.
    /// </summary>
    private (string Encoder, VideoCodec Codec) ResolveEncoder()
    {
        if (_options.Encoder is { } forced)
        {
            var forcedCodec = EncoderProbe.CodecOf(forced)
                ?? throw new EngineFailure(ErrorCodes.EncodeFailed, $"'{forced}' is not an H.264, HEVC or AV1 encoder in this FFmpeg build.");
            _log.LogInformation("Encoder forced to {Encoder} ({Codec})", forced, forcedCodec);
            return (forced, forcedCodec);
        }

        var codec = _spec.Snapshot.Video.Codec;
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

    /// <summary>The first frame is the session anchor: every stream's PTS 0 is its QPC.</summary>
    private void StartOutput(long anchorQpc, VideoSize contentSize)
    {
        _anchorQpc = anchorQpc;
        _log.LogInformation("Anchor QPC {Anchor}; output {Size}, content {Content}", anchorQpc, _outputSize, contentSize);

        _stats?.SnapshotAnchor();
        PacketSink packets;
        var video = _spec.Snapshot.Video;
        if (_options.Replay)
        {
            var window = _spec.Snapshot.BufferWindow;
            _windowSeconds = window;
            var first = NewRing(window);
            _ring = new ReplayRingSink(first);
            packets = _ring;
            _log.LogInformation("Replay Buffer of {Window} s {Storage}, {Capacity} packets preallocated", window, StorageOf(first), RingCapacity(window));
        }
        else
        {
            _muxer = new FragmentedMp4Muxer(_path!, _loggers.CreateLogger<FragmentedMp4Muxer>());
            packets = _muxer;
        }

        var nv12 = _options.Color == ColorPath.X2;
        _frames = new D3D11VAFrames(_device!.NativeDevice, _outputSize.Width, _outputSize.Height, VideoEncoder.PoolSize, nv12);
        _scaler = new FrameScaler(_device, _outputSize, nv12, 2 * VideoEncoder.PoolSize, _loggers.CreateLogger<FrameScaler>());

        _video = new VideoEncoder(_frames, packets, _qpcFrequency, anchorQpc, _loggers.CreateLogger<VideoEncoder>(), _stats?.Latency,
            new VideoEncoderOptions(_options.NvencPreset, _options.NvencTune, _encoderName, _codec, video.Fps, video.BitrateKbps, video.RateControl));
        if (_options.Stages == CaptureStages.Full)
        {
            var pacer = new FramePacer(_qpcFrequency, video.Fps, anchorQpc, VideoEncoder.TimeBase);
            _paced = new PacedVideoEncoder(_video, pacer, _loggers.CreateLogger<PacedVideoEncoder>());
            _paced.Failed += e => Fail(new ErrorCodeException(ErrorCodes.EncodeFailed, "Video encode failed: " + e.Message, e));
        }

        var tracks = _spec.Audio.Tracks;
        for (var i = 0; i < tracks.Count; i++)
        {
            var encoder = new AacEncoder(packets, tracks[i].Name, isDefault: i == 0, _loggers.CreateLogger<AacEncoder>());
            _audioEncoders.Add(encoder);
            _mixers.Add(new TrackMixer(tracks[i].DeviceIndexes.Count, encoder));
        }

        _muxer?.Start();

        // The hub's streams have run since Open; each device takes blocks from here, its held ones
        // replayed first, cut at the anchor once, and fanned out to every Track that holds it.
        var inputs = tracks.Select(t => t.DeviceIndexes).ToList();
        for (var d = 0; d < _audioTracks.Count; d++)
        {
            var sink = TrackWiring.DeviceSink(d, _audioTracks[d].Device.IsMic, tracks, inputs, _mixers, _micMute, _qpcFrequency, anchorQpc);
            var timeline = new AudioTimeline(_qpcFrequency, anchorQpc, sink);
            _audioTimelines.Add(timeline);
            _audioTracks[d].Publish(timeline);
        }

        if (_mixers.Any(m => m.Inputs == 0))
        {
            lock (_silenceGate)
            {
                if (!_silenceStopped)
                {
                    _silenceTimer = new Timer(_ => AdvanceSilence(), null, SilenceInterval, SilenceInterval);
                }
            }
        }

        // A release write: SetMicMuted reads the anchor without the gate.
        Volatile.Write(ref _anchored, true);
        _options.Anchored?.Invoke(anchorQpc);
    }

    private void Finish()
    {
        if ((_muxer is null && _ring is null) || _video is null || _audioEncoders.Count != _spec.Audio.Tracks.Count)
        {
            return;
        }

        _video.Flush();

        // The devices are unsubscribed and the silence clock stopped: pad every Track with silence to
        // one end frame, the longest device's or the video's end, whichever is later (or where the
        // clock already took a silent Track, should that be later still), then drain the encoders.
        // The video ends where its pacer's next slot starts: one past the last emitted slot, skips
        // included, which repeats can take past the last captured frame.
        StopSilenceClock();
        var end = FramesAt(_lastFrameQpc);
        if (_paced?.Pacer is { } slots)
        {
            end = Math.Max(end, (long)((Int128)slots.NextSlot * AudioTimeline.SampleRate / slots.Fps));
        }

        foreach (var mixer in _mixers)
        {
            end = Math.Max(end, mixer.Inputs == 0 ? mixer.EmittedFrames : mixer.LongestInputFrames);
        }

        foreach (var mixer in _mixers)
        {
            mixer.PadTo(end);
        }

        foreach (var audio in _audioEncoders)
        {
            audio.Flush();
        }

        _muxer?.Finish();
        if (_paced?.Pacer is { } pacer)
        {
            _log.LogInformation(
                "Video slots: {Fresh} new, {Repeated} repeated, {Skipped} skipped; {Replaced} held frames replaced, {Stale} stale, {QueueDrops} not drawn",
                pacer.FreshSlots, pacer.RepeatedSlots, pacer.SkippedSlots, pacer.HeldReplaced, pacer.StaleFrames, _paced.QueueDrops + _video.PoolMisses);
        }

        _log.LogInformation("Audio written: {Frames} frames per Track; limited: {Limited} frames per Track",
            string.Join(", ", _audioEncoders.Select(a => a.SamplesWritten)), string.Join(", ", _mixers.Select(m => m.LimitedFrames)));
    }

    /// <summary>Frames since the anchor at <paramref name="qpc"/>; 0 before it.</summary>
    private long FramesAt(long qpc) =>
        qpc <= _anchorQpc ? 0 : (long)((Int128)(qpc - _anchorQpc) * AudioTimeline.SampleRate / _qpcFrequency);

    /// <summary>The silence clock's tick: advances every Track with no devices to the current QPC.</summary>
    private void AdvanceSilence()
    {
        Exception? failure = null;
        lock (_silenceGate)
        {
            if (_silenceStopped)
            {
                return;
            }

            try
            {
                var now = FramesAt(Stopwatch.GetTimestamp());
                foreach (var mixer in _mixers)
                {
                    if (mixer.Inputs == 0)
                    {
                        mixer.PadTo(now);
                    }
                }
            }
            catch (Exception e)
            {
                // The encoder or muxer broke: the clock stops and the session fails.
                _silenceStopped = true;
                failure = e;
            }
        }

        if (failure is not null)
        {
            Fail(new ErrorCodeException(ErrorCodes.EncodeFailed, "Audio encode failed: " + failure.Message, failure));
        }
    }

    /// <summary>Stops the silence clock and waits for a tick in progress; no tick runs after it returns.</summary>
    private void StopSilenceClock()
    {
        Timer? timer;
        lock (_silenceGate)
        {
            _silenceStopped = true;
            timer = _silenceTimer;
            _silenceTimer = null;
        }

        if (timer is null)
        {
            return;
        }

        using var done = new ManualResetEvent(false);
        if (timer.Dispose(done))
        {
            done.WaitOne();
        }
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
    private void WriteStats()
    {
        if (_stats is null || _options.StatsPath is not { } path || _video is null
            || _audioEncoders.Count != _spec.Audio.Tracks.Count || _audioTimelines.Count != _audioTracks.Count)
        {
            return;
        }

        var cadence = _stats.Cadence;
        var arrived = cadence.Frames;
        var stepped = arrived - cadence.NonIncreasing; // frames that advanced the capture clock
        var span = cadence.SpanSeconds;
        var slots = stepped + cadence.MissedSlots;
        var pacer = _paced?.Pacer;
        var emitted = pacer?.EmittedSlots ?? 0;
        var repeated = pacer?.RepeatedSlots ?? 0;
        var video = new VideoReport(
            _outputSize.Width,
            _outputSize.Height,
            arrived,
            _video.EncodedFrames,
            repeated,
            (_paced?.QueueDrops ?? 0) + _video.PoolMisses,
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
        for (var i = 0; i < _audioTracks.Count; i++)
        {
            var track = _audioTracks[i];
            if (track.Subscription is AudioDeviceSubscription { Diagnostics: var diagnostics })
            {
                var role = track.Device.Flow == SessionFlow.Output ? "output" : "input";
                var first = _spec.Audio.FirstTrackOf(i);
                var samples = first >= 0 ? _audioEncoders[first].SamplesWritten : 0;
                audio.Add(CaptureStatsCollector.Report(role, track.Device.FriendlyName, diagnostics.Stamps, diagnostics.Stream, _audioTimelines[i], samples));
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
                _paced is not null),
            video,
            CaptureStatsCollector.Report(_stats.Latency, _video.FlushedPackets),
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

    private void ReportAudioFaults()
    {
        foreach (var track in _audioTracks)
        {
            var name = track.Device.FriendlyName;
            if (track.Subscription?.SubscriberFault is ErrorCodeException { Code: ErrorCodes.EncodeFailed or ErrorCodes.DiskFull or ErrorCodes.AccessDenied } encodeFault)
            {
                // The encoder or muxer broke (Check maps a full disk or denied access to their own codes), not the device: the file is damaged.
                Fail(encodeFault);
            }
            else if (track.Subscription?.SubscriberFault is { } trackFault)
            {
                _log.LogWarning("{Code}: the session's tap on '{Name}' stopped taking audio: {Message}",
                    ErrorCodes.AudioDeviceFailed, name, trackFault.Message);
            }

            if (track.Subscription?.DeviceFault is { } fault)
            {
                _log.LogWarning("{Code}: '{Name}' failed during capture and was held silent: {Message}",
                    ErrorCodes.AudioDeviceFailed, name, fault.Message);
            }
        }
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

    /// <summary>Subscribes a tap to its device; it holds its last second of blocks until the anchor publishes its timeline.</summary>
    private void Subscribe(SessionAudioDevice device)
    {
        var track = new SessionAudioTrack(device);
        try
        {
            track.Subscription = _audio.Subscribe(device.EndpointId, device.Flow, track);
        }
        catch (ErrorCodeException e)
        {
            throw new EngineFailure(e.Code, e.Message);
        }

        _audioTracks.Add(track);
    }

    /// <summary>Microseconds since the anchor at <paramref name="qpc"/>.</summary>
    private long MicrosAt(long qpc) => (long)((Int128)(qpc - _anchorQpc) * 1_000_000 / _qpcFrequency);

    /// <summary>The packets a ring of <paramref name="seconds"/> preallocates: the window and its slack, capped at 2^24.</summary>
    private int RingCapacity(int seconds)
    {
        var perSecond = _spec.Snapshot.Video.Fps + (_spec.Audio.Tracks.Count * AacPacketsPerSecond) + 1;
        return (int)Math.Min((seconds + 3L) * perSecond, 1 << 24);
    }

    /// <summary>An empty Replay Buffer ring of a <paramref name="seconds"/> s Buffer Window, on disk in Disk mode (R3) unless its file can't be made.</summary>
    private PacketRing NewRing(int seconds) => ReplayRings.Create(
        seconds * 1_000_000L,
        RingCapacity(seconds),
        _ringFolder,
        ReplayStorageEstimate.Bytes(_spec.Snapshot.Video, _spec.Audio.Tracks.Count, seconds),
        _log);

    /// <summary>Where a ring keeps its bytes, for the log.</summary>
    private static string StorageOf(PacketRing ring) => ring.File is { } file
        ? $"on disk in {file.Path} ({file.Bytes / (1024 * 1024)} MiB)"
        : "in memory";

    /// <summary>The Buffer Window from the next Reset on (AD-8); the orchestrator calls it just before a Reset.</summary>
    public void SetBufferWindow(int seconds) => _pendingWindow = seconds;

    /// <summary>
    /// At the Reset <paramref name="us"/> (orchestrator thread): moves the Replay Buffer to a ring of
    /// the Buffer Window <see cref="SetBufferWindow"/> asked for, seeded from the Reset keyframe on,
    /// when that differs from the ring's. Nothing on a drained session, or one without a ring.
    /// </summary>
    private void ResizeAt(long us)
    {
        var pending = _pendingWindow;
        if (_keepRing || _outputFinished || _ring is not { } ring || pending < 1 || pending == _windowSeconds)
        {
            return;
        }

        var next = NewRing(pending);
        int generation;
        try
        {
            generation = ring.Resize(next, us);
        }
        catch
        {
            next.Dispose();
            throw;
        }

        _log.LogInformation(
            "Buffer Window {Old} s -> {New} s at the Reset {Reset:0.000} s after the anchor: ring generation {Generation} {Storage}, {Capacity} packets preallocated",
            _windowSeconds, pending, us / 1e6, generation, StorageOf(next), RingCapacity(pending));
        _windowSeconds = pending;
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

    private void DisposeAll()
    {
        // The device threads write into the encoders: unsubscribe before anything they touch is freed.
        for (var i = _audioTracks.Count - 1; i >= 0; i--)
        {
            _audioTracks[i].Subscription?.Dispose();
        }

        // The silence clock writes into the encoders too.
        StopSilenceClock();
        for (var i = _audioEncoders.Count - 1; i >= 0; i--)
        {
            _audioEncoders[i].Dispose();
        }

        _paced?.Dispose(); // joined before the encoder it submits to is freed
        _video?.Dispose();
        _scaler?.Dispose();
        _frames?.Dispose();
        _muxer?.Dispose();

        // A save still writing holds its own lease and pins; the ring goes when the last is released.
        // A drained session keeps it for saves until Release.
        if (!_keepRing)
        {
            _ring?.Dispose();
        }

        _device?.Dispose();
    }

    /// <summary>
    /// One save's write (AD-8): it leases the ring on the orchestrator thread, so ending the session
    /// can't free it, then waits, cuts (pinning the slabs and releasing the lease) and writes on a
    /// background thread of its own, which never blocks the ring. <see cref="Write"/> requests the
    /// keyframe after the press before that thread starts; with no <paramref name="video"/> (a
    /// drained session) it asks for none, and the Clip ends at the press.
    /// </summary>
    private sealed class ClipWrite(CaptureSession session, ReplayRingSink ring, VideoEncoder? video, long qpc, int windowSeconds) : IClipWrite
    {
        // Where the written Clip ends, in µs since the anchor; set before done is called.
        private long _endUs = long.MinValue;

        // The keyframe request's ticket, taken in Write before the writer thread starts.
        private long _ticket;

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
            if (!ring.Lease())
            {
                throw new InvalidOperationException("The Replay Buffer has already been released.");
            }

            try
            {
                // Only a write that starts asks for its keyframe; starting the thread publishes the ticket to it.
                if (video is not null)
                {
                    _ticket = video.RequestKeyframe(qpc);
                }

                new Thread(() => done(Run(path))) { Name = "ClipWriter", IsBackground = true }.Start();
            }
            catch
            {
                ring.Release();
                throw;
            }
        }

        private Exception? Run(string path)
        {
            var log = session._log;
            var leased = true;
            long? claimed = null;
            try
            {
                var pressUs = session.MicrosAt(qpc);
                var toUs = WaitForKey(pressUs);

                // Claimed as soon as the end is known: a later write starts here, not back over this one.
                var previous = session._claim.Claim(toUs);
                claimed = toUs;

                // Every packet whose centre is before the key has been appended once each stream is an AAC frame past it.
                var deadline = Deadline();
                while (!ring.AllStreamsPast(toUs + AacFrameUs) && !session._outputFinished && Stopwatch.GetTimestamp() < deadline)
                {
                    Thread.Sleep(10);
                }

                var windowUs = windowSeconds * 1_000_000L;
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

                using var source = ring.Cut(fromUs, toUs);
                ring.Release();
                leased = false;
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
            }
        }

        /// <summary>
        /// The forced keyframe's start in µs since the anchor, once the encoder has taken a frame
        /// after the request; the press itself when that takes longer than <see cref="SaveWait"/> or
        /// the output has finished first.
        /// </summary>
        private long WaitForKey(long pressUs)
        {
            if (video is null)
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
}
