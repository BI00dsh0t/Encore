using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Encore.Contracts.Settings;

namespace Encore.Engine;

/// <summary>Reads <c>debug-capture</c>'s options into <see cref="CaptureArguments"/>, one parser per group of options.</summary>
internal static class CaptureOptionParser
{
    private static readonly (string Name, bool Duplication)[] MonitorApis = [("wgc", false), ("dd", true)];

    private static readonly (string Name, CaptureStages Stages)[] StageNames =
        [("full", CaptureStages.Full), ("copy", CaptureStages.Copy), ("wgc", CaptureStages.Wgc)];

    private static readonly (string Name, ColorPath Color)[] ColorNames = [("x1", ColorPath.X1), ("x2", ColorPath.X2)];

    private static readonly (string Name, VideoCodec Codec)[] CodecNames =
        [("h264", VideoCodec.H264), ("hevc", VideoCodec.Hevc), ("av1", VideoCodec.Av1)];

    /// <summary>Sets an option that takes no value; false when the option isn't one.</summary>
    public static bool TrySetFlag(CaptureArguments arguments, string option)
    {
        switch (option)
        {
            case "--encode-thread":
                arguments.EncodeThread = true;
                return true;
            case "--track-layout":
                arguments.TrackLayout = true;
                return true;
            case "--match-source":
                arguments.MatchSource = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads an option and its value; returns why it was refused, or null when it was taken.</summary>
    public static string? Apply(CaptureArguments arguments, string option, string value) => option switch
    {
        "--window" or "--hwnd" or "--monitors" or "--monitor-api" => Target(arguments, option, value),
        "--seconds" or "--output" or "--stats" or "--instance" => Run(arguments, option, value),
        "--output-device" or "--input-device" => Audio(arguments, option, value),
        "--gpu-priority" or "--stages" or "--nvenc-preset" or "--nvenc-tune" or "--color" or "--encoder" =>
            Encoder(arguments, option, value),
        "--codec" or "--size" or "--fps" or "--bitrate" => Video(arguments, option, value),
        _ => $"Unknown option '{option}'.",
    };

    /// <summary>The refusals that need every option read first; null when the arguments make a command.</summary>
    public static string? Check(CaptureArguments arguments)
    {
        var target = arguments.Monitors is not null
            ? arguments.Window is not null || arguments.Hwnd is not null
            : (arguments.Window is null) == (arguments.Hwnd is null);
        if (target)
        {
            return "Give exactly one of --window, --hwnd or --monitors.";
        }

        if (arguments.Window is { Length: 0 })
        {
            return "--window needs a non-empty title.";
        }

        if (arguments.TrackLayout && (arguments.OutputDevice is not null || arguments.InputDevice is not null))
        {
            return "--track-layout takes its devices from the Track Layout; drop --output-device and --input-device.";
        }

        // The video key's own check, so debug-capture refuses exactly what settings.update would.
        var size = arguments.Size;
        var video = new VideoSettings(
            arguments.Codec,
            size?.Width ?? 2,
            size?.Height ?? 2,
            arguments.Fps,
            arguments.BitrateKbps,
            RateControl.Cbr);
        return Keys.Video.IsValid(Keys.Video.ToJson(video))
            ? null
            : "The --codec, --size, --fps and --bitrate values don't make a valid video setting.";
    }

    /// <summary>What is captured: one window, or the monitors composited into one canvas.</summary>
    private static string? Target(CaptureArguments arguments, string option, string value)
    {
        switch (option)
        {
            case "--window":
                arguments.Window = value;
                break;
            case "--hwnd":
                if (!CommandLine.TryParseHandle(value, out var handle))
                {
                    return $"'{value}' is not a window handle.";
                }

                arguments.Hwnd = handle;
                break;
            case "--monitors":
                // "all" (every attached monitor) or GDI device names.
                var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (value != "all" && names.Length == 0)
                {
                    return "--monitors needs all or a comma-separated list of monitor device names.";
                }

                arguments.Monitors = value == "all" ? [] : names;
                break;
            case "--monitor-api":
                if (!TryChoose(value, MonitorApis, out var duplication))
                {
                    return "--monitor-api must be wgc or dd.";
                }

                arguments.MonitorDuplication = duplication;
                break;
        }

        return null;
    }

    /// <summary>How long the run is, where its file and stats go, and which test instance it uses.</summary>
    private static string? Run(CaptureArguments arguments, string option, string value)
    {
        switch (option)
        {
            case "--seconds":
                if (!TryReadWhole(value, out var seconds) || seconds <= 0)
                {
                    return $"'{value}' is not a positive number of seconds.";
                }

                arguments.Seconds = seconds;
                break;
            case "--output":
                arguments.Output = value;
                break;
            case "--stats":
                if (value.Length == 0)
                {
                    return "--stats needs a file path.";
                }

                arguments.Stats = value;
                break;
            case "--instance":
                if (!Contracts.Instance.IsTestName(value))
                {
                    return $"--instance must be {ServeCommand.TestInstancePrefix}<letters, digits or dashes>.";
                }

                arguments.Instance = value;
                break;
        }

        return null;
    }

    private static string? Audio(CaptureArguments arguments, string option, string value)
    {
        if (option == "--output-device")
        {
            arguments.OutputDevice = value;
        }
        else
        {
            arguments.InputDevice = value;
        }

        return null;
    }

    /// <summary>How the frames are encoded: GPU priority, stages, NVENC preset and tune, colour path and encoder.</summary>
    private static string? Encoder(CaptureArguments arguments, string option, string value)
    {
        switch (option)
        {
            case "--gpu-priority":
                return GpuPriority(arguments, value);
            case "--stages":
                if (!TryChoose(value, StageNames, out var stages))
                {
                    return $"'{value}' is not one of full, copy or wgc.";
                }

                arguments.Stages = stages;
                break;
            case "--nvenc-preset":
                if (value is not ("p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7"))
                {
                    return $"'{value}' is not an NVENC preset from p1 to p7.";
                }

                arguments.NvencPreset = value;
                break;
            case "--nvenc-tune":
                if (value is not ("hq" or "ll" or "ull"))
                {
                    return $"'{value}' is not one of hq, ll or ull.";
                }

                arguments.NvencTune = value;
                break;
            case "--color":
                if (!TryChoose(value, ColorNames, out var color))
                {
                    return $"'{value}' is not one of x1 or x2.";
                }

                arguments.Color = color;
                break;
            case "--encoder":
                if (value.Length == 0)
                {
                    return "--encoder needs an FFmpeg encoder name.";
                }

                arguments.Encoder = value;
                break;
        }

        return null;
    }

    private static string? GpuPriority(CaptureArguments arguments, string value)
    {
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var priority)
            || priority < DebugCaptureCommand.MinGpuPriority
            || priority > DebugCaptureCommand.MaxGpuPriority)
        {
            return $"'{value}' is not a GPU priority from {DebugCaptureCommand.MinGpuPriority} to {DebugCaptureCommand.MaxGpuPriority}.";
        }

        arguments.GpuPriority = priority;
        return null;
    }

    /// <summary>The video value: codec, size, frame rate and bitrate.</summary>
    private static string? Video(CaptureArguments arguments, string option, string value)
    {
        switch (option)
        {
            case "--codec":
                if (!TryChoose(value, CodecNames, out var codec))
                {
                    return $"'{value}' is not one of h264, hevc or av1.";
                }

                arguments.Codec = codec;
                break;
            case "--size":
                return Size(arguments, value);
            case "--fps":
                if (!TryReadWhole(value, out var fps) || fps is < 1 or > VideoSettings.MaxFps)
                {
                    return $"'{value}' is not a frame rate from 1 to {VideoSettings.MaxFps}.";
                }

                arguments.Fps = fps;
                break;
            case "--bitrate":
                if (!TryReadWhole(value, out var bitrate) || bitrate < 1)
                {
                    return $"'{value}' is not a positive bitrate in kbps.";
                }

                arguments.BitrateKbps = bitrate;
                break;
        }

        return null;
    }

    private static string? Size(CaptureArguments arguments, string value)
    {
        var parts = value.Split('x');
        if (parts.Length != 2
            || !TryReadWhole(parts[0], out var width)
            || !TryReadWhole(parts[1], out var height)
            || !IsVideoDimension(width) || !IsVideoDimension(height))
        {
            return $"'{value}' is not a size WxH with even sides from 2 to {VideoSettings.MaxDimension}.";
        }

        arguments.Size = new DebugCaptureSize(width, height);
        return null;
    }

    private static bool IsVideoDimension(int value) => value is >= 2 and <= VideoSettings.MaxDimension && value % 2 == 0;

    /// <summary>A whole number with no sign, spaces or separators.</summary>
    private static bool TryReadWhole(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static bool TryChoose<T>(string value, (string Name, T Value)[] choices, [MaybeNullWhen(false)] out T chosen)
    {
        foreach (var choice in choices)
        {
            if (choice.Name == value)
            {
                chosen = choice.Value;
                return true;
            }
        }

        chosen = default;
        return false;
    }
}
