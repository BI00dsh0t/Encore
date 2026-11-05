using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Core.Audio;
using ReplayBuffer.Core.Replay;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Audio;
using ReplayBuffer.Media;
using ReplayBuffer.Platform.Capture;
using ReplayBuffer.Platform.FFmpeg;
using VideoCodec = ReplayBuffer.Contracts.Settings.VideoCodec;

namespace ReplayBuffer.Engine.Sessions;

/// <summary>
/// One encoder chain of a <see cref="CaptureSession"/> (4.9): everything one spec encodes with. Its
/// device taps, each cut by one <see cref="AudioTimeline"/> and fanned out to every Track that holds
/// it; each Track's <see cref="TrackMixer"/> and <see cref="AacEncoder"/>; the frame pool, scaler
/// and paced video encoder at the spec's output size; and the Replay Buffer's
/// <see cref="ReplayRingSink"/> behind a <see cref="RecordingTap"/>, or the file's muxer, or (the
/// Monitor session, 6.9) a tap with a <see cref="NullPacketSink"/> behind it. Every PTS is
/// relative to the one session anchor, so Clips of two chains meet on one timeline. The session's
/// first chain starts at the anchor, video and audio alike. A later one is built in advance and
/// stays idle: its audio starts at the save press that switches the session to it
/// (<see cref="StartAudio"/>), its frames at the first one captured after that press, in the slot
/// nearest it. A Track with no devices is silence, advanced from QPC every
/// <see cref="SilenceInterval"/> by a timer, off the capture thread. A chain rebuilt by a forced
/// encoder restart (R4) takes over its predecessor's <see cref="ReplayRingSink"/> in a new ring
/// generation. The session's locks guard the writer count and the retirement fields.
/// </summary>
internal sealed class EncoderChain : IDisposable
{
    /// <summary>AAC packets a second (48 kHz in 1024-sample frames), rounded up, for sizing the ring.</summary>
    private const int AacPacketsPerSecond = 47;

    /// <summary>How often a Track with no devices is advanced to the current QPC.</summary>
    private static readonly TimeSpan SilenceInterval = TimeSpan.FromMilliseconds(100);

    private readonly CaptureSessionOptions _options;
    private readonly D3D11Device _device;
    private readonly IAudioDeviceHub _audio;
    private readonly MicMuteSchedule _micMute;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly Action<Exception> _fail;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly string? _path;
    private readonly string? _ringFolder;
    private readonly List<SessionAudioTrack> _audioTracks = []; // one per device
    private readonly List<AacEncoder> _audioEncoders = []; // one per Track
    private readonly List<TrackMixer> _mixers = []; // one per Track
    private readonly List<AudioTimeline> _audioTimelines = [];
    private readonly Lock _silenceGate = new();
    private Timer? _silenceTimer;
    private bool _silenceStopped;
    private D3D11VAFrames? _frames;
    private FrameScaler? _scaler;
    private VideoEncoder? _video;
    private PacedVideoEncoder? _paced;
    private FragmentedMp4Muxer? _muxer;
    private ReplayRingSink? _ring;
    private RecordingTap? _tap;
    private long _sessionAnchorQpc;
    private long _audioAnchorQpc;
    private int _disposed;

    /// <param name="path">The file a session with no Replay Buffer writes; null in Replay mode.</param>
    /// <param name="fail">Called with an encode failure: the session fails.</param>
    public EncoderChain(
        SessionSpec spec,
        string encoderName,
        VideoCodec codec,
        string? path,
        CaptureSessionOptions options,
        D3D11Device device,
        IAudioDeviceHub audio,
        MicMuteSchedule micMute,
        ILoggerFactory loggers,
        ILogger log,
        Action<Exception> fail)
    {
        Spec = spec;
        EncoderName = encoderName;
        Codec = codec;
        OutputSize = new VideoSize(spec.Snapshot.Video.Width, spec.Snapshot.Video.Height);
        FrameUs = (1_000_000L + spec.Snapshot.Video.Fps - 1) / spec.Snapshot.Video.Fps;
        _path = path;
        _options = options;
        _device = device;
        _audio = audio;
        _micMute = micMute;
        _loggers = loggers;
        _log = log;
        _fail = fail;
        if (options.Replay && spec.Kind != SessionKind.Monitor && spec.Snapshot.ReplayStorage == Contracts.Settings.ReplayStorage.Disk)
        {
            _ringFolder = spec.Snapshot.RingPath ?? options.RingFolder;
            if (_ringFolder is null)
            {
                log.LogWarning("The Disk-mode Replay Buffer has no ring folder; buffering in memory");
            }
        }
    }

    public SessionSpec Spec { get; }

    public string EncoderName { get; }

    public VideoCodec Codec { get; }

    public VideoSize OutputSize { get; private set; }

    /// <summary>
    /// Story 12.1 (matchSource): sets the output size before <see cref="Build"/> creates the frame
    /// pool and scaler at it. A built chain keeps its size.
    /// </summary>
    public void SizeTo(VideoSize size)
    {
        if (IsBuilt)
        {
            throw new InvalidOperationException("A built encoder chain keeps its output size.");
        }

        OutputSize = size;
    }

    public int Fps => Spec.Snapshot.Video.Fps;

    /// <summary>One frame of the spec's fps in µs, rounded up.</summary>
    public long FrameUs { get; }

    /// <summary>Whether <see cref="Build"/> has opened every encoder.</summary>
    public bool IsBuilt { get; private set; }

    /// <summary>Whether it has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Where its audio starts: the session anchor for the first chain, the switching press for a later one.</summary>
    public long AudioAnchorQpc => _audioAnchorQpc;

    /// <summary>The audio anchor as frames since the session anchor: its first AAC sample's PTS.</summary>
    public long StartSample { get; private set; }

    /// <summary>The Buffer Window its ring holds, in seconds; a resize at a Reset moves it.</summary>
    public int WindowSeconds { get; set; }

    public ReplayRingSink? Ring => _ring;

    public RecordingTap? Tap => _tap;

    public VideoEncoder? Video => _video;

    public PacedVideoEncoder? Paced => _paced;

    public IReadOnlyList<SessionAudioTrack> AudioTracks => _audioTracks;

    public IReadOnlyList<AacEncoder> AudioEncoders => _audioEncoders;

    public IReadOnlyList<AudioTimeline> AudioTimelines => _audioTimelines;

    // The session's chain bookkeeping, under its chain lock: saves bound to this chain and not yet
    // cut, whether it was replaced and its retirement has begun, the chain that replaced it, and the
    // end of the Clip its switching press cut (µs since the anchor; long.MinValue when unknown).
    public int Writers { get; set; }

    public bool Retiring { get; set; }

    public bool RetireStarted { get; set; }

    public EncoderChain? Successor { get; set; }

    public long SwitchEndUs { get; set; } = long.MinValue;

    /// <summary>Whether the capture thread still draws into it; under the session's gate.</summary>
    public bool Fed { get; set; } = true;

    /// <summary>Subscribes a tap to each of the spec's devices; each holds its last second of blocks until <see cref="StartAudio"/>.</summary>
    public void Subscribe()
    {
        foreach (var device in Spec.Audio.Devices)
        {
            var track = new SessionAudioTrack(device);
            try
            {
                track.Subscription = _audio.Subscribe(device, track);
            }
            catch (ErrorCodeException e)
            {
                throw new EngineFailure(e.Code, e.Message);
            }

            _audioTracks.Add(track);
        }
    }

    /// <summary>
    /// Opens the outputs and every encoder, its PTS relative to <paramref name="sessionAnchorQpc"/>.
    /// The first chain's pacer gives its first frame slot 0; a later one's, the slot nearest it.
    /// </summary>
    /// <param name="latency">The stats' latency histogram, for the first chain of a measured run.</param>
    /// <param name="restartOf">
    /// A forced restart's predecessor's ring, whose encoders have stopped writing: this chain takes it
    /// over in a new generation (<see cref="ReplayRingSink.Restart"/>) of <paramref name="window"/>
    /// seconds, instead of a ring of its own; null for a new ring.
    /// </param>
    /// <param name="window">The Buffer Window the restarted ring holds, in seconds; with <paramref name="restartOf"/> only.</param>
    public void Build(long sessionAnchorQpc, bool first, LatencyHistogram? latency, ReplayRingSink? restartOf = null, int window = 0)
    {
        _sessionAnchorQpc = sessionAnchorQpc;
        PacketSink packets;
        var video = Spec.Snapshot.Video;
        if (Spec.Kind == SessionKind.Monitor)
        {
            // The Monitor session (6.9): no ring and no file of its own, only its Recording.
            _tap = new RecordingTap(new NullPacketSink());
            packets = _tap;
            _log.LogInformation("Monitor capture: the encoders feed only its Recording");
        }
        else if (_options.Replay && restartOf is not null)
        {
            WindowSeconds = window;
            var ring = NewRing(window);
            int generation;
            try
            {
                generation = restartOf.Restart(ring);
            }
            catch
            {
                ring.Dispose();
                throw;
            }

            _ring = restartOf;
            _tap = new RecordingTap(_ring);
            packets = _tap;
            _log.LogInformation("Replay Buffer restarted in ring generation {Generation}: {Window} s {Storage}, {Capacity} packets preallocated",
                generation, window, StorageOf(ring), RingCapacity(window));
        }
        else if (_options.Replay)
        {
            var bufferWindow = Spec.Snapshot.BufferWindow;
            WindowSeconds = bufferWindow;
            var ring = NewRing(bufferWindow);
            _ring = new ReplayRingSink(ring);
            _tap = new RecordingTap(_ring);
            packets = _tap;
            _log.LogInformation("Replay Buffer of {Window} s {Storage}, {Capacity} packets preallocated", bufferWindow, StorageOf(ring), RingCapacity(bufferWindow));
        }
        else
        {
            _muxer = new FragmentedMp4Muxer(_path!, _loggers.CreateLogger<FragmentedMp4Muxer>());
            packets = _muxer;
        }

        var nv12 = _options.Color == ColorPath.X2;
        _frames = new D3D11VAFrames(_device.NativeDevice, OutputSize.Width, OutputSize.Height, VideoEncoder.PoolSize, nv12);
        _scaler = new FrameScaler(_device, OutputSize, nv12, 2 * VideoEncoder.PoolSize, _loggers.CreateLogger<FrameScaler>());

        _video = new VideoEncoder(_frames, packets, _qpcFrequency, sessionAnchorQpc, _loggers.CreateLogger<VideoEncoder>(), latency,
            new VideoEncoderOptions(_options.NvencPreset, _options.NvencTune, EncoderName, Codec, video.Fps, video.BitrateKbps, video.RateControl));
        if (_options.Stages == CaptureStages.Full)
        {
            var pacer = new FramePacer(_qpcFrequency, video.Fps, sessionAnchorQpc, VideoEncoder.TimeBase, firstFrameTakesNearestSlot: !first);
            _paced = new PacedVideoEncoder(_video, pacer, _loggers.CreateLogger<PacedVideoEncoder>());
            _paced.Failed += e => _fail(new ErrorCodeException(ErrorCodes.EncodeFailed, "Video encode failed: " + e.Message, e));
        }

        var tracks = Spec.Audio.Tracks;
        for (var i = 0; i < tracks.Count; i++)
        {
            var encoder = new AacEncoder(packets, tracks[i].Name, isDefault: i == 0, _loggers.CreateLogger<AacEncoder>());
            _audioEncoders.Add(encoder);
            _mixers.Add(new TrackMixer(tracks[i].DeviceIndexes.Count, encoder));
        }

        _muxer?.Start();
        IsBuilt = true;
    }

    /// <summary>
    /// Starts the audio at <paramref name="audioAnchorQpc"/>: each device takes blocks from here,
    /// its held ones replayed first and cut there once, fanned out to every Track that holds it; mic
    /// mute positions count from it, and each Track's first sample has its frames since the session
    /// anchor as its PTS. A Track with no devices starts the silence clock.
    /// </summary>
    public void StartAudio(long audioAnchorQpc)
    {
        _audioAnchorQpc = audioAnchorQpc;
        StartSample = audioAnchorQpc <= _sessionAnchorQpc
            ? 0
            : (long)((Int128)(audioAnchorQpc - _sessionAnchorQpc) * AudioTimeline.SampleRate / _qpcFrequency);
        if (StartSample > 0)
        {
            foreach (var encoder in _audioEncoders)
            {
                encoder.StartAt(StartSample);
            }
        }

        var tracks = Spec.Audio.Tracks;
        var inputs = tracks.Select(t => t.DeviceIndexes).ToList();
        for (var d = 0; d < _audioTracks.Count; d++)
        {
            var sink = TrackWiring.DeviceSink(d, _audioTracks[d].Device.IsMic, tracks, inputs, _mixers, _micMute, _qpcFrequency, audioAnchorQpc);
            var timeline = new AudioTimeline(_qpcFrequency, audioAnchorQpc, sink);
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
    }

    /// <summary>
    /// Scales one captured frame into the chain's output and posts it to the encoder thread; under
    /// the session's gate, on the capture thread. A full hand-off drops the frame.
    /// </summary>
    public void Draw(long qpc, nint texture, VideoSize contentSize)
    {
        if (_paced is null)
        {
            // --stages copy: the scale alone; submit it, then hand the texture back unencoded.
            if (_video!.TryAcquireFrame(out var drawn))
            {
                try
                {
                    _scaler!.Draw(texture, contentSize, drawn.Texture, drawn.Subresource);
                    _device.Flush();
                }
                finally
                {
                    VideoEncoder.Release(drawn);
                }
            }

            return;
        }

        // Only draw here; the encoder thread paces, submits and drains.
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

    /// <summary>
    /// Takes the Recording tap out of the chain, so <see cref="Dispose"/> leaves it to the caller: a
    /// forced restart closes it, ending the Recording, only once the orchestrator has been told.
    /// </summary>
    public RecordingTap? TakeTap()
    {
        var tap = _tap;
        _tap = null;
        return tap;
    }

    /// <summary>No frame can arrive now: lets the encoder thread offer what is queued before the flush.</summary>
    public void CompleteVideo() => _paced?.Complete();

    /// <summary>Unsubscribes every tap; once a subscription is disposed, its track gets no further block.</summary>
    public void StopTaps()
    {
        for (var i = _audioTracks.Count - 1; i >= 0; i--)
        {
            _audioTracks[i].Subscription?.Dispose();
        }
    }

    /// <summary>
    /// Flushes the chain into its output once the taps are stopped: every Track is padded with
    /// silence to one end frame, the longest device's or the video's end, whichever is later (or
    /// where the clock already took a silent Track, should that be later still), then the encoders
    /// are drained. The video ends where its pacer's next slot starts: one past the last emitted
    /// slot, skips included, which repeats can take past the last captured frame.
    /// </summary>
    public void Finish(long lastFrameQpc)
    {
        if (!IsBuilt || (_muxer is null && _ring is null && _tap is null) || _video is null || _audioEncoders.Count != Spec.Audio.Tracks.Count)
        {
            return;
        }

        _video.Flush();
        StopSilenceClock();
        var end = FramesAt(lastFrameQpc);
        if (_paced?.Pacer is { } slots)
        {
            end = Math.Max(end, (long)((Int128)slots.NextSlot * AudioTimeline.SampleRate / slots.Fps) - StartSample);
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

    /// <summary>Logs each tap's device fault, and fails the session when the encoder or muxer behind one broke.</summary>
    public void ReportAudioFaults()
    {
        foreach (var track in _audioTracks)
        {
            var name = track.Device.FriendlyName;
            if (track.Subscription?.SubscriberFault is ErrorCodeException { Code: ErrorCodes.EncodeFailed or ErrorCodes.DiskFull or ErrorCodes.AccessDenied } encodeFault)
            {
                // The encoder or muxer broke (Check maps a full disk or denied access to their own codes), not the device: the file is damaged.
                _fail(encodeFault);
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

    /// <summary>
    /// The start a Clip from this chain cuts from, for <paramref name="fromUs"/> (µs since the
    /// anchor): its first keyframe when that is less than one frame before it, else
    /// <paramref name="fromUs"/>. A chain that started mid-session may give its first frame the slot
    /// before the old chain's last key; cutting from that key would skip to its next keyframe.
    /// </summary>
    public long SnapStart(long fromUs)
    {
        var first = _ring?.FirstKeyUs ?? long.MaxValue;
        return first != long.MaxValue && first < fromUs && fromUs <= first + FrameUs ? first : fromUs;
    }

    /// <summary>The ring of a <paramref name="seconds"/> s Buffer Window, as <see cref="Build"/> makes it; for a resize at a Reset.</summary>
    public PacketRing NewRing(int seconds) => ReplayRings.Create(
        seconds * 1_000_000L,
        RingCapacity(seconds),
        _ringFolder,
        ReplayStorageEstimate.Bytes(Spec.Snapshot.Video, Spec.Audio.Tracks.Count, seconds),
        _options.Margin,
        _log);

    /// <summary>The packets a ring of <paramref name="seconds"/> preallocates: the window and its slack, capped at 2^24.</summary>
    public int RingCapacity(int seconds)
    {
        var perSecond = Spec.Snapshot.Video.Fps + (Spec.Audio.Tracks.Count * AacPacketsPerSecond) + 1;
        return (int)Math.Min((seconds + 3L) * perSecond, 1 << 24);
    }

    /// <summary>Where a ring keeps its bytes, for the log.</summary>
    public static string StorageOf(PacketRing ring) => ring.File is { } file
        ? $"on disk in {file.Path} ({file.Bytes / (1024 * 1024)} MiB)"
        : "in memory";

    /// <summary>Stops the silence clock and waits for a tick in progress; no tick runs after it returns.</summary>
    public void StopSilenceClock()
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

    public void Dispose() => Dispose(keepRing: false);

    /// <summary>
    /// Frees everything, once, with no flush; with <paramref name="keepRing"/> (a drained session)
    /// the ring is kept for saves until the session's <c>Release</c>. A save still writing holds its
    /// own lease and pins, and a Recording still finishing owns its own copies of the streams.
    /// </summary>
    public void Dispose(bool keepRing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The device threads write into the encoders: unsubscribe before anything they touch is freed.
        StopTaps();

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
        _tap?.Dispose();
        if (!keepRing)
        {
            _ring?.Dispose();
        }
    }

    /// <summary>Frames since the audio anchor at <paramref name="qpc"/>; 0 before it.</summary>
    private long FramesAt(long qpc) =>
        qpc <= _audioAnchorQpc ? 0 : (long)((Int128)(qpc - _audioAnchorQpc) * AudioTimeline.SampleRate / _qpcFrequency);

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
            _fail(new ErrorCodeException(ErrorCodes.EncodeFailed, "Audio encode failed: " + failure.Message, failure));
        }
    }
}
