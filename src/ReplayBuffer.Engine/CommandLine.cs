using System.Globalization;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Engine;

/// <summary>A parsed engine command.</summary>
public abstract record EngineCommand;

/// <summary><c>debug-list</c>: windows and audio endpoints.</summary>
public sealed record DebugListCommand : EngineCommand;

/// <summary>Private AD-28 media worker, isolated from live engine startup.</summary>
public sealed record ClipMediaWorkerCommand : EngineCommand;

/// <summary>How much of the video path a <c>debug-capture</c> runs (story 1.2 cost split).</summary>
public enum CaptureStages
{
    /// <summary>WGC → copy → NVENC → file: the tracer.</summary>
    Full,

    /// <summary>WGC → copy into the encoder's pool; nothing is encoded.</summary>
    Copy,

    /// <summary>WGC frames are taken and released; no copy, no encode.</summary>
    Wgc,
}

/// <summary>How captured BGRA reaches NVENC (PLAN.md §6.2).</summary>
public enum ColorPath
{
    /// <summary>BGRA straight into NVENC, which converts in hardware.</summary>
    X1,

    /// <summary>BGRA → NV12 on the D3D11 VideoProcessor, NV12 into NVENC.</summary>
    X2,
}

/// <summary>
/// <c>debug-capture</c>: the tracer capture of one window. <see cref="Codec"/>, <see cref="Size"/>,
/// <see cref="Fps"/> and <see cref="BitrateKbps"/> make the session's <c>video</c> value (CBR); a null
/// <see cref="Size"/> is the window's size, made even. <see cref="Encoder"/> forces an FFmpeg encoder.
/// <see cref="EncodeThread"/> (<c>--encode-thread</c>) is still accepted and has no effect: since
/// story 1.11 the encoder thread is always on. <see cref="TrackLayout"/> (<c>--track-layout</c>)
/// captures the Tracks of the instance's Global <c>trackLayout</c> instead of the output and input
/// device; <see cref="Instance"/> (<c>--instance</c>, test names only) overrides the process's own instance.
/// </summary>
public sealed record DebugCaptureCommand(
    string? Window,
    nint? Hwnd,
    int Seconds,
    string? Output,
    string? OutputDevice,
    string? InputDevice,
    string? Stats = null,
    int? GpuPriority = null,
    CaptureStages Stages = CaptureStages.Full,
    string NvencPreset = DebugCaptureCommand.DefaultNvencPreset,
    string? NvencTune = null,
    ColorPath Color = ColorPath.X1,
    bool EncodeThread = false,
    VideoCodec Codec = VideoCodec.H264,
    DebugCaptureSize? Size = null,
    int Fps = DebugCaptureCommand.DefaultFps,
    int BitrateKbps = DebugCaptureCommand.DefaultBitrateKbps,
    string? Encoder = null,
    bool TrackLayout = false,
    string? Instance = null,
    IReadOnlyList<string>? Monitors = null,
    bool MonitorDuplication = false,
    bool MatchSource = false) : EngineCommand
{
    public const int DefaultSeconds = 30;

    /// <summary>The tracer's frame rate and bitrate, kept as <c>debug-capture</c>'s defaults.</summary>
    public const int DefaultFps = 60;

    public const int DefaultBitrateKbps = 20_000;

    public const string DefaultNvencPreset = "p5";

    /// <summary>The range <c>IDXGIDevice::SetGPUThreadPriority</c> accepts.</summary>
    public const int MinGpuPriority = -7;

    public const int MaxGpuPriority = 7;
}

/// <summary><c>debug-capture --size</c>: the output size, <c>WxH</c>.</summary>
public sealed record DebugCaptureSize(int Width, int Height);

/// <summary>
/// <c>serve</c>: the engine proper, serving its pipe until <c>engine.shutdown</c> or Ctrl+C.
/// <see cref="Instance"/> overrides the process's own instance, for tests only. <see cref="DebugCapture"/>
/// (<c>--debug-capture</c>) runs a capture session on the Target Game's window, following it, until
/// the Replay Buffer does (epic 4); it also turns Game Detection on for a test instance.
/// <see cref="ReplayBuffer"/> (<c>--replay-buffer</c>, test instances only) turns the Replay Buffer,
/// and with it Game Detection, on for the test instance, so a separate engine process can save Clips.
/// <see cref="Restarted"/> (<c>--restarted crashed|hung</c>) is how the App relaunches an engine that
/// crashed or hung: the engine raises <c>engine-restarted</c> before its pipe serves (AD-16).
/// </summary>
public sealed record ServeCommand(string? Instance, bool DebugCapture = false, bool ReplayBuffer = false, EngineRestartReason? Restarted = null) : EngineCommand
{
    /// <summary>The only instance names <c>--instance</c> accepts, so tests never touch a real instance.</summary>
    public const string TestInstancePrefix = Contracts.Instance.TestPrefix;
}

/// <summary>The outcome of parsing: a command, or the reason there isn't one.</summary>
public sealed record ParseResult(EngineCommand? Command, string? Error);

/// <summary>Parses the engine's debug command line.</summary>
public static class CommandLine
{
    public const string Usage =
        """
        Usage:
          ReplayBuffer.Engine serve [--instance ReplayBuffer-Test-<suffix>] [--debug-capture | --replay-buffer (needs --instance)]
                                    [--restarted crashed|hung]
          ReplayBuffer.Engine debug-list
          ReplayBuffer.Engine debug-capture (--window <title> | --hwnd <handle>) [--seconds 30]
                              [--output <file.mp4>] [--output-device <name>] [--input-device <name>]
                              [--stats <file.json>] [--gpu-priority <-7..7>] [--stages full|copy|wgc]
                              [--nvenc-preset p1..p7] [--nvenc-tune hq|ll|ull] [--color x1|x2] [--encode-thread]
                              [--codec h264|hevc|av1] [--size <W>x<H>] [--fps 1..120] [--bitrate <kbps>]
                              [--encoder <ffmpeg encoder name>] [--track-layout] [--match-source]
                              [--instance ReplayBuffer-Test-<suffix>]
          ReplayBuffer.Engine debug-capture --monitors all|<device name>[,...] [--monitor-api wgc|dd] [the options above]
                              (spike 11.1, dev only: the monitors composited into one canvas instead of a window;
                              no --size: the canvas, fitted only to the codec's largest side: 4096 px for H.264, 8192 for HEVC and AV1)
        """;

    public static ParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Fail("No command given.");
        }

        return args[0] switch
        {
            "clip-media-worker" => args.Count == 1 ? new ParseResult(new ClipMediaWorkerCommand(), null) : Fail("The clip worker takes no arguments."),
            "debug-list" => args.Count == 1 ? new ParseResult(new DebugListCommand(), null) : Fail($"Unexpected argument '{args[1]}'."),
            "debug-capture" => ParseCapture(args),
            "serve" => ParseServe(args),
            _ => Fail($"Unknown command '{args[0]}'."),
        };
    }

    /// <summary>Parses a window handle in hex (<c>0x1A2B</c>) or decimal.</summary>
    public static bool TryParseHandle(string text, out nint handle)
    {
        handle = 0;
        var parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        if (!parsed || value == 0)
        {
            return false;
        }

        handle = (nint)value;
        return true;
    }

    private static ParseResult ParseCapture(IReadOnlyList<string> args)
    {
        string? window = null, output = null, outputDevice = null, inputDevice = null, stats = null;
        nint? hwnd = null;
        int? gpuPriority = null;
        var stages = CaptureStages.Full;
        var preset = DebugCaptureCommand.DefaultNvencPreset;
        string? tune = null;
        var color = ColorPath.X1;
        var encodeThread = false;
        var seconds = DebugCaptureCommand.DefaultSeconds;
        var codec = VideoCodec.H264;
        DebugCaptureSize? size = null;
        var fps = DebugCaptureCommand.DefaultFps;
        var bitrate = DebugCaptureCommand.DefaultBitrateKbps;
        string? encoder = null;
        var trackLayout = false;
        string? instance = null;
        IReadOnlyList<string>? monitors = null;
        var monitorDuplication = false;
        var matchSource = false;

        for (var i = 1; i < args.Count; i++)
        {
            var option = args[i];
            if (option == "--encode-thread")
            {
                encodeThread = true;
                continue;
            }

            if (option == "--track-layout")
            {
                trackLayout = true;
                continue;
            }

            if (option == "--match-source")
            {
                matchSource = true;
                continue;
            }

            if (i + 1 >= args.Count)
            {
                return Fail($"Option '{option}' needs a value.");
            }

            var value = args[++i];
            switch (option)
            {
                case "--window":
                    window = value;
                    break;
                case "--monitors":
                    // Spike 11.1: "all" (every attached monitor) or GDI device names.
                    monitors = value == "all" ? [] : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    if (value != "all" && monitors.Count == 0)
                    {
                        return Fail("--monitors needs all or a comma-separated list of monitor device names.");
                    }

                    break;
                case "--monitor-api":
                    switch (value)
                    {
                        case "wgc":
                            monitorDuplication = false;
                            break;
                        case "dd":
                            monitorDuplication = true;
                            break;
                        default:
                            return Fail("--monitor-api must be wgc or dd.");
                    }

                    break;
                case "--hwnd":
                    if (!TryParseHandle(value, out var handle))
                    {
                        return Fail($"'{value}' is not a window handle.");
                    }

                    hwnd = handle;
                    break;
                case "--seconds":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds <= 0)
                    {
                        return Fail($"'{value}' is not a positive number of seconds.");
                    }

                    break;
                case "--output":
                    output = value;
                    break;
                case "--output-device":
                    outputDevice = value;
                    break;
                case "--input-device":
                    inputDevice = value;
                    break;
                case "--stats":
                    if (value.Length == 0)
                    {
                        return Fail("--stats needs a file path.");
                    }

                    stats = value;
                    break;
                case "--gpu-priority":
                    if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var priority)
                        || priority < DebugCaptureCommand.MinGpuPriority
                        || priority > DebugCaptureCommand.MaxGpuPriority)
                    {
                        return Fail($"'{value}' is not a GPU priority from {DebugCaptureCommand.MinGpuPriority} to {DebugCaptureCommand.MaxGpuPriority}.");
                    }

                    gpuPriority = priority;
                    break;
                case "--stages":
                    switch (value)
                    {
                        case "full":
                            stages = CaptureStages.Full;
                            break;
                        case "copy":
                            stages = CaptureStages.Copy;
                            break;
                        case "wgc":
                            stages = CaptureStages.Wgc;
                            break;
                        default:
                            return Fail($"'{value}' is not one of full, copy or wgc.");
                    }

                    break;
                case "--nvenc-preset":
                    if (value is not ("p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7"))
                    {
                        return Fail($"'{value}' is not an NVENC preset from p1 to p7.");
                    }

                    preset = value;
                    break;
                case "--nvenc-tune":
                    if (value is not ("hq" or "ll" or "ull"))
                    {
                        return Fail($"'{value}' is not one of hq, ll or ull.");
                    }

                    tune = value;
                    break;
                case "--color":
                    switch (value)
                    {
                        case "x1":
                            color = ColorPath.X1;
                            break;
                        case "x2":
                            color = ColorPath.X2;
                            break;
                        default:
                            return Fail($"'{value}' is not one of x1 or x2.");
                    }

                    break;
                case "--codec":
                    switch (value)
                    {
                        case "h264":
                            codec = VideoCodec.H264;
                            break;
                        case "hevc":
                            codec = VideoCodec.Hevc;
                            break;
                        case "av1":
                            codec = VideoCodec.Av1;
                            break;
                        default:
                            return Fail($"'{value}' is not one of h264, hevc or av1.");
                    }

                    break;
                case "--size":
                    var parts = value.Split('x');
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                        || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
                        || !IsVideoDimension(width) || !IsVideoDimension(height))
                    {
                        return Fail($"'{value}' is not a size WxH with even sides from 2 to {VideoSettings.MaxDimension}.");
                    }

                    size = new DebugCaptureSize(width, height);
                    break;
                case "--fps":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out fps) || fps is < 1 or > VideoSettings.MaxFps)
                    {
                        return Fail($"'{value}' is not a frame rate from 1 to {VideoSettings.MaxFps}.");
                    }

                    break;
                case "--bitrate":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out bitrate) || bitrate < 1)
                    {
                        return Fail($"'{value}' is not a positive bitrate in kbps.");
                    }

                    break;
                case "--encoder":
                    if (value.Length == 0)
                    {
                        return Fail("--encoder needs an FFmpeg encoder name.");
                    }

                    encoder = value;
                    break;
                case "--instance":
                    if (!Contracts.Instance.IsTestName(value))
                    {
                        return Fail($"--instance must be {ServeCommand.TestInstancePrefix}<letters, digits or dashes>.");
                    }

                    instance = value;
                    break;
                default:
                    return Fail($"Unknown option '{option}'.");
            }
        }

        if (monitors is not null ? window is not null || hwnd is not null : (window is null) == (hwnd is null))
        {
            return Fail("Give exactly one of --window, --hwnd or --monitors.");
        }

        if (window is { Length: 0 })
        {
            return Fail("--window needs a non-empty title.");
        }

        if (trackLayout && (outputDevice is not null || inputDevice is not null))
        {
            return Fail("--track-layout takes its devices from the Track Layout; drop --output-device and --input-device.");
        }

        // The video key's own check, so debug-capture refuses exactly what settings.update would.
        var video = new VideoSettings(codec, size?.Width ?? 2, size?.Height ?? 2, fps, bitrate, RateControl.Cbr);
        if (!Keys.Video.IsValid(Keys.Video.ToJson(video)))
        {
            return Fail("The --codec, --size, --fps and --bitrate values don't make a valid video setting.");
        }

        return new ParseResult(
            new DebugCaptureCommand(
                window, hwnd, seconds, output, outputDevice, inputDevice, stats, gpuPriority, stages, preset, tune, color, encodeThread,
                codec, size, fps, bitrate, encoder, trackLayout, instance, monitors, monitorDuplication, matchSource),
            null);
    }

    private static bool IsVideoDimension(int value) => value is >= 2 and <= VideoSettings.MaxDimension && value % 2 == 0;

    private static ParseResult ParseServe(IReadOnlyList<string> args)
    {
        string? name = null;
        var debugCapture = false;
        var replayBuffer = false;
        EngineRestartReason? restarted = null;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--restarted" when restarted is null && i + 1 < args.Count:
                    restarted = args[++i] switch
                    {
                        "crashed" => EngineRestartReason.Crashed,
                        "hung" => EngineRestartReason.Hung,
                        _ => null,
                    };
                    if (restarted is null)
                    {
                        return Fail($"'{args[i]}' is not one of crashed or hung.");
                    }

                    break;
                case "--debug-capture" when !debugCapture:
                    debugCapture = true;
                    break;
                case "--replay-buffer" when !replayBuffer:
                    replayBuffer = true;
                    break;
                case "--instance" when name is null && i + 1 < args.Count:
                    name = args[++i];
                    if (!Contracts.Instance.IsTestName(name))
                    {
                        return Fail($"--instance must be {ServeCommand.TestInstancePrefix}<letters, digits or dashes>.");
                    }

                    break;
                default:
                    return Fail("serve takes only --instance <name>, --replay-buffer, --debug-capture and --restarted crashed|hung.");
            }
        }

        if (replayBuffer && name is null)
        {
            return Fail("--replay-buffer is for test instances only; give --instance ReplayBuffer-Test-<suffix>.");
        }

        if (replayBuffer && debugCapture)
        {
            return Fail("--replay-buffer and --debug-capture can't be combined: a debug capture turns the Replay Buffer off.");
        }

        return new ParseResult(new ServeCommand(name, debugCapture, replayBuffer, restarted), null);
    }

    private static ParseResult Fail(string error) => new(null, error);
}
