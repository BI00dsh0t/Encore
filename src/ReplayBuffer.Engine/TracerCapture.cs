using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Media;
using ReplayBuffer.Platform.Audio;
using ReplayBuffer.Platform.Capture;
using ReplayBuffer.Platform.FFmpeg;
using ReplayBuffer.Platform.Windows;

namespace ReplayBuffer.Engine;

/// <summary>A failure to report with its code, and the candidates the user can pick from.</summary>
public sealed class EngineFailure(string code, string message, IReadOnlyList<string>? candidates = null) : Exception(message)
{
    public string Code { get; } = code;

    public IReadOnlyList<string> Candidates { get; } = candidates ?? [];
}

/// <summary>
/// The tracer's direct path (story 1.1): WGC window → NVENC, one loopback Output Device and
/// one Input Device → AAC, all into one fragmented MP4. Encoding runs on the capture threads,
/// a shortcut that stories 6, 11 and 12 replace.
/// </summary>
public sealed class TracerCapture(DebugCaptureCommand command, Instance instance, ILoggerFactory loggers)
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _log = loggers.CreateLogger<TracerCapture>();
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly ManualResetEventSlim _firstFrame = new();
    private readonly ManualResetEventSlim _stop = new();
    private readonly Lock _gate = new();

    private D3D11Device? _device;
    private WasapiCapture? _outputCapture;
    private WasapiCapture? _inputCapture;
    private string _path = "";
    private D3D11VAFrames? _frames;
    private FragmentedMp4Muxer? _muxer;
    private VideoEncoder? _video;
    private AacEncoder? _outputAudio;
    private AacEncoder? _inputAudio;
    private VideoSize _outputSize;
    private long _anchorQpc;
    private long _lastFrameQpc;
    private Exception? _failure;
    private string _stopReason = "";

    /// <summary>Runs the capture and returns the path of the finished file.</summary>
    public string Run(CancellationToken cancellation)
    {
        FFmpegLibrary.Initialize(loggers.CreateLogger("FFmpeg"));

        var window = ResolveWindow();
        var output = ResolveEndpoint(AudioFlow.Output, command.OutputDevice);
        var input = ResolveEndpoint(AudioFlow.Input, command.InputDevice);
        _log.LogInformation("Window '{Title}' (0x{Handle:X}, {Process}); output '{Output}'; input '{Input}'",
            window.Title, window.Handle, window.ProcessName, output.FriendlyName, input.FriendlyName);

        _path = command.Output is { } explicitPath
            ? Path.GetFullPath(explicitPath)
            : OutputNaming.Allocate(Path.Combine(instance.LocalDataFolder, "debug"), window.Title, DateTime.Now, File.Exists);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        try
        {
            _device = D3D11Device.Create(window.Handle, loggers.CreateLogger<D3D11Device>());
            _outputCapture = OpenDevice(output);
            _inputCapture = OpenDevice(input);

            using (var capture = new WindowCapture(_device, window.Handle, OnFrame, loggers.CreateLogger<WindowCapture>()))
            using (cancellation.Register(() => RequestStop("Ctrl+C")))
            {
                capture.Closed += () => RequestStop("window closed");
                capture.Failed += e => Fail(e);
                capture.Start();

                WaitHandle.WaitAny([_firstFrame.WaitHandle, _stop.WaitHandle], FirstFrameTimeout);

                // A first frame still opening the encoders holds the gate; let it finish.
                bool started;
                lock (_gate)
                {
                    started = _firstFrame.IsSet;
                }

                if (!started)
                {
                    var reason = _failure?.Message
                        ?? (_stop.IsSet
                            ? $"Capture stopped ({_stopReason}) before the first frame."
                            : $"No frame arrived from the window within {FirstFrameTimeout.TotalSeconds:0} s.");
                    throw new EngineFailure(_failure is null ? ErrorCodes.CaptureFailed : CodeOf(_failure), reason);
                }

                // --seconds counts from the anchor, not from when the encoders finished opening.
                var elapsed = (double)(Stopwatch.GetTimestamp() - _anchorQpc) / _qpcFrequency;
                var remaining = TimeSpan.FromSeconds(Math.Max(0, command.Seconds - elapsed));
                if (!_stop.Wait(remaining))
                {
                    RequestStop($"{command.Seconds} s elapsed");
                }

                _log.LogInformation("Stopping: {Reason}", _stopReason);
            }

            // Capture is stopped and no frame is in flight; stop audio, then drain everything.
            _outputCapture.Stop();
            _inputCapture.Stop();
            ReportAudioFaults();
            Finish();
        }
        finally
        {
            DisposeAll();
        }

        if (_failure is not null)
        {
            throw _failure as EngineFailure ?? new EngineFailure(CodeOf(_failure), _failure.Message);
        }

        _log.LogInformation("Wrote {Seconds:0.0} s to {Path}",
            (double)(_lastFrameQpc - _anchorQpc) / _qpcFrequency, _path);
        return _path;
    }

    private void OnFrame(long qpc, nint texture, VideoSize contentSize)
    {
        lock (_gate)
        {
            if (_stop.IsSet)
            {
                return;
            }

            if (_video is null)
            {
                StartSession(qpc, contentSize);
            }

            using var surface = _video!.TryAcquire(qpc);
            if (surface is null)
            {
                return;
            }

            _device!.CopyFrame(texture, contentSize, surface.Texture, surface.Subresource, _outputSize);
            _video.Submit(surface);
            _lastFrameQpc = qpc;
        }
    }

    /// <summary>The first frame is the session anchor: every stream's PTS 0 is its QPC.</summary>
    private void StartSession(long anchorQpc, VideoSize contentSize)
    {
        _anchorQpc = anchorQpc;
        _outputSize = VideoSize.EvenFloor(contentSize.Width, contentSize.Height);
        _log.LogInformation("Anchor QPC {Anchor}; output {Size} from content {Content}", anchorQpc, _outputSize, contentSize);

        _muxer = new FragmentedMp4Muxer(_path, loggers.CreateLogger<FragmentedMp4Muxer>());
        _frames = new D3D11VAFrames(_device!.NativeDevice, _outputSize.Width, _outputSize.Height, VideoEncoder.PoolSize);
        _video = new VideoEncoder(_frames, _muxer, _qpcFrequency, anchorQpc, loggers.CreateLogger<VideoEncoder>());
        _outputAudio = new AacEncoder(_muxer, _outputCapture!.Endpoint.FriendlyName, isDefault: true, loggers.CreateLogger<AacEncoder>());
        _inputAudio = new AacEncoder(_muxer, _inputCapture!.Endpoint.FriendlyName, isDefault: false, loggers.CreateLogger<AacEncoder>());
        _muxer.Start();

        _outputCapture.Start(new AudioTimeline(_qpcFrequency, anchorQpc, _outputAudio));
        _inputCapture.Start(new AudioTimeline(_qpcFrequency, anchorQpc, _inputAudio));
        _firstFrame.Set();
    }

    private void Finish()
    {
        if (_muxer is null || _video is null || _outputAudio is null || _inputAudio is null)
        {
            return;
        }

        _video.Flush();
        _outputAudio.Flush();
        _inputAudio.Flush();
        _muxer.Finish();
        _log.LogInformation("Audio written: output {Output} frames, input {Input} frames; video frames dropped: {Dropped}",
            _outputAudio.SamplesWritten, _inputAudio.SamplesWritten, _video.DroppedFrames);
    }

    private void ReportAudioFaults()
    {
        foreach (var capture in new[] { _outputCapture, _inputCapture })
        {
            if (capture?.Fault is ErrorCodeException { Code: ErrorCodes.EncodeFailed } encodeFault)
            {
                // The encoder or muxer broke, not the device: the file is damaged.
                Fail(encodeFault);
            }
            else if (capture?.Fault is { } fault)
            {
                _log.LogWarning("{Code}: '{Name}' failed during capture and was held silent: {Message}",
                    ErrorCodes.AudioDeviceFailed, capture.Endpoint.FriendlyName, fault.Message);
            }
        }
    }

    private void RequestStop(string reason)
    {
        lock (_gate)
        {
            if (_stop.IsSet)
            {
                return;
            }

            _stopReason = reason;
            _stop.Set();
        }
    }

    private void Fail(Exception e)
    {
        _failure ??= e;
        RequestStop("failure: " + e.Message);
    }

    private WasapiCapture OpenDevice(AudioEndpoint endpoint)
    {
        try
        {
            return new WasapiCapture(endpoint, loggers.CreateLogger<WasapiCapture>());
        }
        catch (ErrorCodeException e)
        {
            throw new EngineFailure(e.Code, e.Message);
        }
    }

    private WindowInfo ResolveWindow()
    {
        if (command.Hwnd is { } handle)
        {
            return WindowFinder.FromHandle(handle)
                ?? throw new EngineFailure(ErrorCodes.WindowNotFound, $"0x{handle:X} is not a visible, titled top-level window.",
                    WindowFinder.List().Select(Describe).ToList());
        }

        var match = WindowFinder.Match(WindowFinder.List(), command.Window!);
        return match.Outcome switch
        {
            WindowMatchOutcome.Found => match.Window!,
            WindowMatchOutcome.NotFound => throw new EngineFailure(ErrorCodes.WindowNotFound,
                $"No window title contains '{command.Window}'.", match.Candidates.Select(Describe).ToList()),
            _ => throw new EngineFailure(ErrorCodes.WindowAmbiguous,
                $"{match.Candidates.Count} window titles contain '{command.Window}'; use a longer title or --hwnd.",
                match.Candidates.Select(Describe).ToList()),
        };
    }

    private static AudioEndpoint ResolveEndpoint(AudioFlow flow, string? query)
    {
        try
        {
            if (query is null)
            {
                return AudioEndpoints.Default(flow);
            }

            var endpoints = AudioEndpoints.List(flow);
            var match = AudioEndpoints.Match(endpoints, query);
            var kind = flow == AudioFlow.Output ? "output" : "input";
            return match.Outcome switch
            {
                EndpointMatchOutcome.Found => match.Endpoint!,
                EndpointMatchOutcome.NotFound => throw new EngineFailure(ErrorCodes.AudioDeviceNotFound,
                    $"No {kind} device name contains '{query}'.", match.Candidates.Select(Describe).ToList()),
                _ => throw new EngineFailure(ErrorCodes.AudioDeviceAmbiguous,
                    $"{match.Candidates.Count} {kind} device names contain '{query}'; use the full name.",
                    match.Candidates.Select(Describe).ToList()),
            };
        }
        catch (Exception e) when (e is not EngineFailure)
        {
            throw new EngineFailure(ErrorCodes.AudioDeviceFailed, $"Could not list {flow} devices: {e.Message}");
        }
    }

    internal static string Describe(WindowInfo w) => $"0x{w.Handle:X8}  {w.ProcessName ?? "?",-24} {w.Title}";

    internal static string Describe(AudioEndpoint e) => $"{e.FriendlyName}  [{e.Id}]";

    private static string CodeOf(Exception e) => e switch
    {
        EngineFailure f => f.Code,
        ErrorCodeException c => c.Code,
        _ => ErrorCodes.EncodeFailed,
    };

    private void DisposeAll()
    {
        // The WASAPI threads write into the encoders: join them before anything they touch is freed.
        _inputCapture?.Dispose();
        _outputCapture?.Dispose();
        _inputAudio?.Dispose();
        _outputAudio?.Dispose();
        _video?.Dispose();
        _frames?.Dispose();
        _muxer?.Dispose();
        _device?.Dispose();
        _firstFrame.Dispose();
        _stop.Dispose();
    }
}
