using System.Globalization;
using Encore.Contracts.Messages;

namespace Encore.Engine;

/// <summary>Parses the engine's debug command line.</summary>
public static class CommandLine
{
    public const string Usage =
        """
        Usage:
          Encore.Engine serve [--instance Encore-Test-<suffix>] [--debug-capture | --rewind (needs --instance)]
                                    [--restarted crashed|hung]
          Encore.Engine debug-list
          Encore.Engine debug-capture (--window <title> | --hwnd <handle>) [--seconds 30]
                              [--output <file.mp4>] [--output-device <name>] [--input-device <name>]
                              [--stats <file.json>] [--gpu-priority <-7..7>] [--stages full|copy|wgc]
                              [--nvenc-preset p1..p7] [--nvenc-tune hq|ll|ull] [--color x1|x2] [--encode-thread]
                              [--codec h264|hevc|av1] [--size <W>x<H>] [--fps 1..120] [--bitrate <kbps>]
                              [--encoder <ffmpeg encoder name>] [--track-layout] [--match-source]
                              [--instance Encore-Test-<suffix>]
          Encore.Engine debug-capture --monitors all|<device name>[,...] [--monitor-api wgc|dd] [the options above]
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
            "clip-media-worker" => args.Count == 1
                ? new ParseResult(new ClipMediaWorkerCommand(), null)
                : Fail("The clip worker takes no arguments."),
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
        var arguments = new CaptureArguments();
        for (var i = 1; i < args.Count; i++)
        {
            var option = args[i];
            if (CaptureOptionParser.TrySetFlag(arguments, option))
            {
                continue;
            }

            if (i + 1 >= args.Count)
            {
                return Fail($"Option '{option}' needs a value.");
            }

            if (CaptureOptionParser.Apply(arguments, option, args[++i]) is { } refusal)
            {
                return Fail(refusal);
            }
        }

        return CaptureOptionParser.Check(arguments) is { } problem
            ? Fail(problem)
            : new ParseResult(arguments.ToCommand(), null);
    }

    private static ParseResult ParseServe(IReadOnlyList<string> args)
    {
        string? name = null;
        var debugCapture = false;
        var rewind = false;
        EngineRestartReason? restarted = null;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--restarted" when restarted is null && i + 1 < args.Count:
                    restarted = RestartReason(args[++i]);
                    if (restarted is null)
                    {
                        return Fail($"'{args[i]}' is not one of crashed or hung.");
                    }

                    break;
                case "--debug-capture" when !debugCapture:
                    debugCapture = true;
                    break;
                case "--rewind" when !rewind:
                    rewind = true;
                    break;
                case "--instance" when name is null && i + 1 < args.Count:
                    name = args[++i];
                    if (!Contracts.Instance.IsTestName(name))
                    {
                        return Fail($"--instance must be {ServeCommand.TestInstancePrefix}<letters, digits or dashes>.");
                    }

                    break;
                default:
                    return Fail("serve takes only --instance <name>, --rewind, --debug-capture and --restarted crashed|hung.");
            }
        }

        return ServeRefusal(name, debugCapture, rewind) is { } refusal
            ? Fail(refusal)
            : new ParseResult(new ServeCommand(name, debugCapture, rewind, restarted), null);
    }

    private static EngineRestartReason? RestartReason(string value) => value switch
    {
        "crashed" => EngineRestartReason.Crashed,
        "hung" => EngineRestartReason.Hung,
        _ => null,
    };

    private static string? ServeRefusal(string? name, bool debugCapture, bool rewind)
    {
        if (rewind && name is null)
        {
            return "--rewind is for test instances only; give --instance Encore-Test-<suffix>.";
        }

        return rewind && debugCapture
            ? "--rewind and --debug-capture can't be combined: a debug capture turns Rewind off."
            : null;
    }

    private static ParseResult Fail(string error) => new(null, error);
}
