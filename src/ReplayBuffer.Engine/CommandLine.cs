using System.Globalization;

namespace ReplayBuffer.Engine;

/// <summary>A parsed engine command.</summary>
public abstract record EngineCommand;

/// <summary><c>debug-list</c>: windows and audio endpoints.</summary>
public sealed record DebugListCommand : EngineCommand;

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

/// <summary><c>debug-capture</c>: the tracer capture of one window.</summary>
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
    bool EncodeThread = false) : EngineCommand
{
    public const int DefaultSeconds = 30;

    public const string DefaultNvencPreset = "p5";

    /// <summary>The range <c>IDXGIDevice::SetGPUThreadPriority</c> accepts.</summary>
    public const int MinGpuPriority = -7;

    public const int MaxGpuPriority = 7;
}

/// <summary>
/// <c>serve</c>: the engine proper, serving its pipe until <c>engine.shutdown</c> or Ctrl+C.
/// <see cref="Instance"/> overrides the process's own instance, for tests only.
/// </summary>
public sealed record ServeCommand(string? Instance) : EngineCommand
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
          ReplayBuffer.Engine serve [--instance ReplayBuffer-Test-<suffix>]
          ReplayBuffer.Engine debug-list
          ReplayBuffer.Engine debug-capture (--window <title> | --hwnd <handle>) [--seconds 30]
                              [--output <file.mp4>] [--output-device <name>] [--input-device <name>]
                              [--stats <file.json>] [--gpu-priority <-7..7>] [--stages full|copy|wgc]
                              [--nvenc-preset p1..p7] [--nvenc-tune hq|ll|ull] [--color x1|x2] [--encode-thread]
        """;

    public static ParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Fail("No command given.");
        }

        return args[0] switch
        {
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

        for (var i = 1; i < args.Count; i++)
        {
            var option = args[i];
            if (option == "--encode-thread")
            {
                encodeThread = true;
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
                default:
                    return Fail($"Unknown option '{option}'.");
            }
        }

        if ((window is null) == (hwnd is null))
        {
            return Fail("Give exactly one of --window or --hwnd.");
        }

        if (window is { Length: 0 })
        {
            return Fail("--window needs a non-empty title.");
        }

        return new ParseResult(new DebugCaptureCommand(window, hwnd, seconds, output, outputDevice, inputDevice, stats, gpuPriority, stages, preset, tune, color, encodeThread), null);
    }

    private static ParseResult ParseServe(IReadOnlyList<string> args)
    {
        if (args.Count == 1)
        {
            return new ParseResult(new ServeCommand(null), null);
        }

        if (args.Count != 3 || args[1] != "--instance")
        {
            return Fail("serve takes only --instance <name>.");
        }

        var name = args[2];
        if (!Contracts.Instance.IsTestName(name))
        {
            return Fail($"--instance must be {ServeCommand.TestInstancePrefix}<letters, digits or dashes>.");
        }

        return new ParseResult(new ServeCommand(name), null);
    }

    private static ParseResult Fail(string error) => new(null, error);
}
