using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Audio;
using Encore.Core.Output;
using Encore.Core.Replay;
using Encore.Core.Sessions;
using Encore.Media;
using Encore.Platform.Capture;
using Encore.Platform.FFmpeg;
using VideoCodec = Encore.Contracts.Settings.VideoCodec;

namespace Encore.Engine.Sessions;

/// <summary>One encoder chain of a session: everything one spec encodes with. Details: docs/code-notes.md.</summary>
internal sealed class EncoderChain : IDisposable
{
    private readonly CaptureSessionOptions _options;
    private readonly D3D11Device _device;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly Action<Exception> _fail;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly string? _path;
    private readonly string? _ringFolder;
    private readonly ChainAudio _audio;
    private D3D11VAFrames? _frames;
    private FrameScaler? _scaler;
    private VideoEncoder? _video;
    private PacedVideoEncoder? _paced;
    private FragmentedMp4Muxer? _muxer;
    private ReplayRingSink? _ring;
    private RecordingTap? _tap;
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
        _loggers = loggers;
        _log = log;
        _fail = fail;
        _audio = new ChainAudio(spec.Audio, audio, micMute, loggers, log, fail);
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

    /// <summary>The Buffer Window its ring holds, in seconds; a resize at a Reset moves it.</summary>
    public int WindowSeconds { get; set; }

    public ReplayRingSink? Ring => _ring;

    public RecordingTap? Tap => _tap;

    public VideoEncoder? Video => _video;

    public PacedVideoEncoder? Paced => _paced;

    public IReadOnlyList<SessionAudioTrack> AudioTracks => _audio.Tracks;

    public IReadOnlyList<AacEncoder> AudioEncoders => _audio.Encoders;

    public IReadOnlyList<AudioTimeline> AudioTimelines => _audio.Timelines;

    // The session's bookkeeping for this chain, under its chain lock: bound saves, retirement and what replaced it.
    public int Writers { get; set; }

    public bool Retiring { get; set; }

    public bool RetireStarted { get; set; }

    public EncoderChain? Successor { get; set; }

    public long SwitchEndUs { get; set; } = long.MinValue;

    /// <summary>Whether the capture thread still draws into it; under the session's gate.</summary>
    public bool Fed { get; set; } = true;

    /// <summary>Subscribes a tap to each of the spec's devices; each holds its last second of blocks until the audio starts.</summary>
    public void Subscribe() => _audio.Subscribe();

    /// <summary>Opens the outputs and every encoder; restartOf hands over a predecessor's ring in a new generation.</summary>
    public void Build(long sessionAnchorQpc, bool first, LatencyHistogram? latency, ReplayRingSink? restartOf = null, int window = 0)
    {
        var packets = OpenOutput(restartOf, window);
        OpenVideo(packets, sessionAnchorQpc, first, latency);
        _audio.Open(packets, sessionAnchorQpc);
        _muxer?.Start();
        IsBuilt = true;
    }

    /// <summary>Starts the audio at a QPC time: each device takes blocks from here, and mic mute positions count from it.</summary>
    public void StartAudio(long audioAnchorQpc) => _audio.Start(audioAnchorQpc);

    /// <summary>Scales one captured frame and posts it to the encoder thread; capture thread, under the session's gate.</summary>
    public void Draw(long qpc, nint texture, VideoSize contentSize)
    {
        if (_paced is null)
        {
            DrawUnencoded(texture, contentSize);
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
    public void StopTaps() => _audio.StopTaps();

    /// <summary>Flushes the chain: pads every Track with silence to one end frame, then drains the encoders.</summary>
    public void Finish(long lastFrameQpc)
    {
        if (!IsBuilt || (_muxer is null && _ring is null && _tap is null) || _video is null || !_audio.IsOpen)
        {
            return;
        }

        _video.Flush();
        StopSilenceClock();
        var end = _audio.FramesAt(lastFrameQpc);
        if (_paced?.Pacer is { } slots)
        {
            end = Math.Max(end, (long)((Int128)slots.NextSlot * AudioTimeline.SampleRate / slots.Fps) - _audio.StartSample);
        }

        _audio.PadAndFlush(end);
        _muxer?.Finish();
        if (_paced?.Pacer is { } pacer)
        {
            _log.LogInformation(
                "Video slots: {Fresh} new, {Repeated} repeated, {Skipped} skipped; {Replaced} held frames replaced, {Stale} stale, {QueueDrops} not drawn",
                pacer.FreshSlots, pacer.RepeatedSlots, pacer.SkippedSlots, pacer.HeldReplaced, pacer.StaleFrames, _paced.QueueDrops + _video.PoolMisses);
        }

        _audio.LogWritten();
    }

    /// <summary>Logs each tap's device fault, and fails the session when the encoder or muxer behind one broke.</summary>
    public void ReportAudioFaults() => _audio.ReportFaults();

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

    /// <summary>The packets its ring of <paramref name="seconds"/> preallocates.</summary>
    public int RingCapacity(int seconds) => ReplayRings.Capacity(Spec.Snapshot.Video.Fps, Spec.Audio.Tracks.Count, seconds);

    /// <summary>Stops the silence clock and waits for a tick in progress; no tick runs after it returns.</summary>
    public void StopSilenceClock() => _audio.StopSilenceClock();

    public void Dispose() => Dispose(keepRing: false);

    /// <summary>Frees everything once, with no flush; keepRing leaves the ring for saves until the session's Release.</summary>
    public void Dispose(bool keepRing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The taps, then the silence clock, then the audio encoders they write into.
        _audio.Dispose();
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

    /// <summary>Opens what the encoders write into: Rewind's ring behind a tap, the Monitor session's tap alone, or the file.</summary>
    private PacketSink OpenOutput(ReplayRingSink? restartOf, int window)
    {
        if (Spec.Kind == SessionKind.Monitor)
        {
            // The Monitor session: no ring and no file of its own, only its Recording.
            _tap = new RecordingTap(new NullPacketSink());
            _log.LogInformation("Monitor capture: the encoders feed only its Recording");
            return _tap;
        }

        if (_options.Replay && restartOf is not null)
        {
            return RestartRing(restartOf, window);
        }

        if (_options.Replay)
        {
            var bufferWindow = Spec.Snapshot.BufferWindow;
            WindowSeconds = bufferWindow;
            var ring = NewRing(bufferWindow);
            _ring = new ReplayRingSink(ring);
            _tap = new RecordingTap(_ring);
            _log.LogInformation("Rewind of {Window} s {Storage}, {Capacity} packets preallocated",
                bufferWindow, ReplayRings.StorageOf(ring), RingCapacity(bufferWindow));
            return _tap;
        }

        _muxer = new FragmentedMp4Muxer(_path!, _loggers.CreateLogger<FragmentedMp4Muxer>());
        return _muxer;
    }

    /// <summary>Takes over a predecessor's ring in a new generation, so what it held stays saveable.</summary>
    private RecordingTap RestartRing(ReplayRingSink restartOf, int window)
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
        _log.LogInformation("Rewind restarted in ring generation {Generation}: {Window} s {Storage}, {Capacity} packets preallocated",
            generation, window, ReplayRings.StorageOf(ring), RingCapacity(window));
        return _tap;
    }

    /// <summary>Makes the frame pool, the scaler and the video encoder, paced unless the run stops at the copy stage.</summary>
    private void OpenVideo(PacketSink packets, long sessionAnchorQpc, bool first, LatencyHistogram? latency)
    {
        var video = Spec.Snapshot.Video;
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
    }

    /// <summary><c>--stages copy</c>: the scale alone; the frame is submitted, then handed back unencoded.</summary>
    private void DrawUnencoded(nint texture, VideoSize contentSize)
    {
        if (!_video!.TryAcquireFrame(out var drawn))
        {
            return;
        }

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
}
