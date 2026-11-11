using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Audio;
using Encore.Core.Output;
using Encore.Core.Replay;
using Encore.Core.Sessions;
using Encore.Engine.Audio;
using Encore.Media;
using Encore.Platform.Capture;
using Encore.Platform.FFmpeg;
using VideoCodec = Encore.Contracts.Settings.VideoCodec;

namespace Encore.Engine.Sessions;

/// <summary>One encoder chain of a session: everything one spec encodes with. Details: docs/code-notes.md.</summary>
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

    /// <summary>The path is the file a session with no Rewind writes; fail is called with an encode failure.</summary>
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
                log.LogWarning("The Disk-mode Rewind has no ring folder; buffering in memory");
            }
        }
    }

    public SessionSpec Spec { get; }

    public string EncoderName { get; }

    public VideoCodec Codec { get; }

    public VideoSize OutputSize { get; private set; }

    /// <summary>Sets the output size before <see cref="Build"/> creates the frame pool and scaler; a built chain keeps its size.</summary>
    public void SizeTo(VideoSize size)
    {
        if (IsBuilt)
        {
            throw new InvalidOperationException("A built encoder chain keeps its output size.");
        }

        OutputSize = size;
    }

    /// <summary>Sizes an unbuilt game chain from the game's captured size when the snapshot has <c>matchSource</c> on.</summary>
    public EncoderChain SizedToSource(bool isMonitor, VideoSize? source)
    {
        if (!isMonitor && Spec.Snapshot.Video.MatchSource && source is { } captured
            && SourceOutputSize.For(captured.Width, captured.Height, Codec) is { } size)
        {
            SizeTo(new VideoSize(size.Width, size.Height));
        }

        return this;
    }

    public int Fps => Spec.Snapshot.Video.Fps;

    /// <summary>One frame of the spec's fps in µs, rounded up.</summary>
    public long FrameUs { get; }

    /// <summary>Whether <see cref="Build"/> has opened every encoder.</summary>
    public bool IsBuilt { get; private set; }

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

    // The session's bookkeeping for this chain, under its chain lock: bound saves, retirement and what replaced it.
    public int Writers { get; set; }

    public bool Retiring { get; set; }

    public bool RetireStarted { get; set; }

    public EncoderChain? Successor { get; set; }

    public long SwitchEndUs { get; set; } = long.MinValue;

    /// <summary>Whether the capture thread still draws into it; under the session's gate.</summary>
    public bool Fed { get; set; } = true;

    /// <summary>Subscribes a tap to each of the spec's devices; each holds its last second of blocks until the audio starts.</summary>
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

    /// <summary>Opens the outputs and every encoder; restartOf hands over a predecessor's ring in a new generation.</summary>
    public void Build(long sessionAnchorQpc, bool first, LatencyHistogram? latency, ReplayRingSink? restartOf = null, int window = 0)
    {
        _sessionAnchorQpc = sessionAnchorQpc;
        PacketSink packets;
        var video = Spec.Snapshot.Video;
        if (Spec.Kind == SessionKind.Monitor)
        {
            // The Monitor session: no ring and no file of its own, only its Recording.
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
            _log.LogInformation("Rewind restarted in ring generation {Generation}: {Window} s {Storage}, {Capacity} packets preallocated",
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
            _log.LogInformation("Rewind of {Window} s {Storage}, {Capacity} packets preallocated", bufferWindow, StorageOf(ring), RingCapacity(bufferWindow));
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

    /// <summary>Starts the audio at a QPC time: each device takes blocks from here, and mic mute positions count from it.</summary>
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

    /// <summary>Scales one captured frame and posts it to the encoder thread; capture thread, under the session's gate.</summary>
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

    /// <summary>Takes the Recording tap out of the chain, so the caller closes it once the orchestrator has been told.</summary>
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

    /// <summary>Flushes the chain: pads every Track with silence to one end frame, then drains the encoders.</summary>
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
                // The encoder or muxer broke, not the device: the file is damaged.
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

    /// <summary>The start a Clip cuts from: the chain's first keyframe when that is under one frame before the given time.</summary>
    public long SnapStart(long fromUs)
    {
        var first = _ring?.FirstKeyUs ?? long.MaxValue;
        return first != long.MaxValue && first < fromUs && fromUs <= first + FrameUs ? first : fromUs;
    }

    /// <summary>The ring of a Buffer Window of this many seconds, as <see cref="Build"/> makes it; for a resize at a Reset.</summary>
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

    /// <summary>Frees everything once, with no flush; keepRing leaves the ring for saves until the session's Release.</summary>
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
