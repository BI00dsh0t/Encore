using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Media;
using ReplayBuffer.Platform.Audio;
using ReplayBuffer.Platform.Capture;
using ReplayBuffer.Platform.FFmpeg;
using ReplayBuffer.Platform.Windows;
using PlatformFlow = ReplayBuffer.Platform.Audio.AudioFlow;
using SessionFlow = ReplayBuffer.Contracts.Settings.AudioFlow;
using VideoCodec = ReplayBuffer.Contracts.Settings.VideoCodec;

namespace ReplayBuffer.Engine.Sessions;

/// <summary>
/// The engine's own session options: where the file goes, and <c>debug-capture</c>'s measurement
/// and encoder experiments. None of it is part of <see cref="SessionSpec"/>.
/// </summary>
internal sealed record CaptureSessionOptions
{
    public static readonly TimeSpan DefaultFirstFrameTimeout = TimeSpan.FromSeconds(5);

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

    /// <summary>With no first frame by then, the session posts a <see cref="ErrorCodes.CaptureFailed"/> failure.</summary>
    public TimeSpan FirstFrameTimeout { get; init; } = DefaultFirstFrameTimeout;
}

/// <summary>
/// Starts <see cref="CaptureSession"/>s with one set of options and one encoder catalog; with no
/// catalog given, the factory probes on its own, once per codec.
/// </summary>
internal sealed class CaptureSessionFactory(CaptureSessionOptions options, ILoggerFactory loggers, IVideoEncoderCatalog? encoders = null)
    : ICaptureSessionFactory
{
    private readonly IVideoEncoderCatalog _encoders = encoders ?? new VideoEncoderCatalog(loggers);

    public ICaptureSession Start(SessionSpec spec, ISessionEvents events)
    {
        FFmpegLibrary.Initialize(loggers.CreateLogger("FFmpeg"));
        var session = new CaptureSession(spec, events, options, _encoders, loggers);
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
/// One capture session: WGC window → scaler → paced video encoder, and each audio device → AAC, all
/// into one fragmented MP4. The first frame is the anchor, every stream's PTS 0. The snapshot's
/// <c>video</c> sets the encoder (the probe's for its codec, else H.264), the output size, the fps,
/// the bitrate and the rate control. The capture thread only scales each frame into the fixed output,
/// letterboxed (<see cref="FrameScaler"/>), and posts it; the encoder thread emits exactly one frame
/// per slot of the fps, repeating the last one when nothing new arrived (story 1.11,
/// <see cref="PacedVideoEncoder"/>). A retarget swaps only the WGC capture, so the file keeps one
/// unbroken set of streams.
/// </summary>
internal sealed class CaptureSession : ICaptureSession
{
    private readonly SessionSpec _spec;
    private readonly ISessionEvents _events;
    private readonly CaptureSessionOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly Lock _gate = new();
    private readonly List<WasapiCapture> _audioCaptures = [];
    private readonly List<AacEncoder> _audioEncoders = [];
    private readonly List<AudioTimeline> _audioTimelines = [];
    private readonly CaptureStatsCollector? _stats;
    private readonly Timer _firstFrameTimer;

    private WindowInfo? _window;
    private string _path = "";
    private D3D11Device? _device;
    private WindowCapture? _capture;
    private D3D11VAFrames? _frames;
    private FrameScaler? _scaler;
    private PacedVideoEncoder? _paced;
    private FragmentedMp4Muxer? _muxer;
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

    public CaptureSession(SessionSpec spec, ISessionEvents events, CaptureSessionOptions options, IVideoEncoderCatalog encoders, ILoggerFactory loggers)
    {
        _spec = spec;
        _events = events;
        _options = options;
        _encoders = encoders;
        _outputSize = new VideoSize(spec.Snapshot.Video.Width, spec.Snapshot.Video.Height);
        _loggers = loggers;
        _log = loggers.CreateLogger<CaptureSession>();
        _stats = options.Stats;
        _firstFrameTimer = new Timer(_ => OnFirstFrameTimeout(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Opens the device and audio clients and starts capture; the output opens on the first frame.</summary>
    public void Open()
    {
        // Before anything is opened: the first codec use may run the probe.
        (_encoderName, _codec) = ResolveEncoder();
        var target = _spec.Target;
        _window = WindowFinder.FromHandle(target.Window);
        _path = _options.OutputPath is { } explicitPath
            ? Path.GetFullPath(explicitPath)
            : OutputNaming.Allocate(_spec.Snapshot.OutputFolder ?? _options.OutputFolder, _window?.Title ?? "Capture", DateTime.Now, File.Exists);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        _device = D3D11Device.Create(target.Window, _loggers.CreateLogger<D3D11Device>());
        if (_options.GpuPriority is { } priority)
        {
            SetGpuPriority(priority);
        }

        foreach (var device in _spec.Audio.Devices)
        {
            _audioCaptures.Add(OpenDevice(device));
        }

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
            foreach (var audio in _audioCaptures)
            {
                audio.Stop();
            }

            ReportAudioFaults();
            Finish();
            WriteStats();
        }
        finally
        {
            DisposeAll();
        }

        if (_failure is null)
        {
            _log.LogInformation("Wrote {Seconds:0.0} s to {Path}", (double)(_lastFrameQpc - _anchorQpc) / _qpcFrequency, _path);
        }

        return new SessionOutcome(_path, _stopReason, _failure);
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
        _muxer = new FragmentedMp4Muxer(_path, _loggers.CreateLogger<FragmentedMp4Muxer>());
        var nv12 = _options.Color == ColorPath.X2;
        _frames = new D3D11VAFrames(_device!.NativeDevice, _outputSize.Width, _outputSize.Height, VideoEncoder.PoolSize, nv12);
        _scaler = new FrameScaler(_device, _outputSize, nv12, 2 * VideoEncoder.PoolSize, _loggers.CreateLogger<FrameScaler>());

        var video = _spec.Snapshot.Video;
        _video = new VideoEncoder(_frames, _muxer, _qpcFrequency, anchorQpc, _loggers.CreateLogger<VideoEncoder>(), _stats?.Latency,
            new VideoEncoderOptions(_options.NvencPreset, _options.NvencTune, _encoderName, _codec, video.Fps, video.BitrateKbps, video.RateControl));
        if (_options.Stages == CaptureStages.Full)
        {
            var pacer = new FramePacer(_qpcFrequency, video.Fps, anchorQpc, VideoEncoder.TimeBase);
            _paced = new PacedVideoEncoder(_video, pacer, _loggers.CreateLogger<PacedVideoEncoder>());
            _paced.Failed += e => Fail(new ErrorCodeException(ErrorCodes.EncodeFailed, "Video encode failed: " + e.Message, e));
        }

        for (var i = 0; i < _audioCaptures.Count; i++)
        {
            _audioEncoders.Add(new AacEncoder(_muxer, _audioCaptures[i].Endpoint.FriendlyName, isDefault: i == 0, _loggers.CreateLogger<AacEncoder>()));
        }

        _muxer.Start();

        for (var i = 0; i < _audioCaptures.Count; i++)
        {
            var timeline = new AudioTimeline(_qpcFrequency, anchorQpc, _audioEncoders[i]);
            _audioTimelines.Add(timeline);
            _audioCaptures[i].Start(timeline, StampsFor(i));
        }

        _anchored = true;
        _options.Anchored?.Invoke(anchorQpc);
    }

    /// <summary>The stats collector holds one stamp record per flow: the first device of each flow gets it.</summary>
    private AudioStampStats? StampsFor(int index)
    {
        if (_stats is null)
        {
            return null;
        }

        var flow = _audioCaptures[index].Endpoint.Flow;
        for (var i = 0; i < index; i++)
        {
            if (_audioCaptures[i].Endpoint.Flow == flow)
            {
                return null;
            }
        }

        return flow == PlatformFlow.Output ? _stats.OutputStamps : _stats.InputStamps;
    }

    private void Finish()
    {
        if (_muxer is null || _video is null || _audioEncoders.Count != _audioCaptures.Count)
        {
            return;
        }

        _video.Flush();
        foreach (var audio in _audioEncoders)
        {
            audio.Flush();
        }

        _muxer.Finish();
        if (_paced?.Pacer is { } pacer)
        {
            _log.LogInformation(
                "Video slots: {Fresh} new, {Repeated} repeated, {Skipped} skipped; {Replaced} held frames replaced, {Stale} stale, {QueueDrops} not drawn",
                pacer.FreshSlots, pacer.RepeatedSlots, pacer.SkippedSlots, pacer.HeldReplaced, pacer.StaleFrames, _paced.QueueDrops + _video.PoolMisses);
        }

        _log.LogInformation("Audio written: {Frames} frames per stream", string.Join(", ", _audioEncoders.Select(a => a.SamplesWritten)));
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
        if (_stats is null || _options.StatsPath is not { } path || _video is null || _audioTimelines.Count != _audioCaptures.Count)
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

        var audio = new List<AudioReport>();
        for (var i = 0; i < _audioCaptures.Count; i++)
        {
            if (StampsFor(i) is { } stamps)
            {
                var capture = _audioCaptures[i];
                var role = capture.Endpoint.Flow == PlatformFlow.Output ? "output" : "input";
                audio.Add(CaptureStatsCollector.Report(role, capture.Endpoint.FriendlyName, stamps, _audioTimelines[i], _audioEncoders[i].SamplesWritten));
            }
        }

        var report = new CaptureStatsReport(
            CaptureStatsReport.CurrentSchema,
            DateTimeOffset.Now,
            _path,
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
        foreach (var capture in _audioCaptures)
        {
            if (capture.Fault is ErrorCodeException { Code: ErrorCodes.EncodeFailed } encodeFault)
            {
                // The encoder or muxer broke, not the device: the file is damaged.
                Fail(encodeFault);
            }
            else if (capture.Fault is { } fault)
            {
                _log.LogWarning("{Code}: '{Name}' failed during capture and was held silent: {Message}",
                    ErrorCodes.AudioDeviceFailed, capture.Endpoint.FriendlyName, fault.Message);
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

    private WasapiCapture OpenDevice(SessionAudioDevice device)
    {
        var flow = device.Flow == SessionFlow.Output ? PlatformFlow.Output : PlatformFlow.Input;
        try
        {
            return new WasapiCapture(new AudioEndpoint(device.EndpointId, device.FriendlyName, flow), _loggers.CreateLogger<WasapiCapture>());
        }
        catch (ErrorCodeException e)
        {
            throw new EngineFailure(e.Code, e.Message);
        }
    }

    private void DisposeAll()
    {
        // The WASAPI threads write into the encoders: join them before anything they touch is freed.
        for (var i = _audioCaptures.Count - 1; i >= 0; i--)
        {
            _audioCaptures[i].Dispose();
        }

        for (var i = _audioEncoders.Count - 1; i >= 0; i--)
        {
            _audioEncoders[i].Dispose();
        }

        _paced?.Dispose(); // joined before the encoder it submits to is freed
        _video?.Dispose();
        _scaler?.Dispose();
        _frames?.Dispose();
        _muxer?.Dispose();
        _device?.Dispose();
    }
}
