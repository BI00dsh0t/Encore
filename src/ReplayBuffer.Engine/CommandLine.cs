using System.Globalization;

namespace ReplayBuffer.Engine;

/// <summary>A parsed engine command.</summary>
public abstract record EngineCommand;

/// <summary><c>debug-list</c>: windows and audio endpoints.</summary>
public sealed record DebugListCommand : EngineCommand;

/// <summary><c>debug-capture</c>: the tracer capture of one window.</summary>
public sealed record DebugCaptureCommand(
    string? Window,
    nint? Hwnd,
    int Seconds,
    string? Output,
    string? OutputDevice,
    string? InputDevice) : EngineCommand
{
    public const int DefaultSeconds = 30;
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
        string? window = null, output = null, outputDevice = null, inputDevice = null;
        nint? hwnd = null;
        var seconds = DebugCaptureCommand.DefaultSeconds;

        for (var i = 1; i < args.Count; i++)
        {
            var option = args[i];
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

        return new ParseResult(new DebugCaptureCommand(window, hwnd, seconds, output, outputDevice, inputDevice), null);
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
