using System.Globalization;

namespace Encore.Bench.GpuLoad;

/// <summary>A command line the load can't run: printed on stderr, exit code 2.</summary>
internal sealed class LoadUsageException(string message) : Exception(message);

/// <summary>
/// How the load presents (story 5.2): a borderless popup (<c>borderless</c>), DXGI fullscreen on the
/// flip-model swap chain, which Windows runs with fullscreen optimizations as most games' "exclusive
/// fullscreen" is today (<c>fullscreen</c>), or legacy exclusive fullscreen on a blt-model swap chain,
/// which Bench starts with fullscreen optimizations off (<c>exclusive</c>).
/// </summary>
internal enum LoadMode
{
    Borderless,
    Fullscreen,
    Exclusive,
}

/// <summary>
/// The load's command line: <c>--target-fps</c> (default 80, spike 1.2's ≈ 78 fps in Cyberpunk at
/// 2560×1440), <c>--parent &lt;pid&gt;</c> (exit when it does), <c>--size WxH</c> (default: the primary
/// monitor), <c>--warmup</c> seconds of calibration and <c>--mode</c> (<see cref="LoadMode"/>, default borderless).
/// </summary>
internal sealed record LoadOptions(double TargetFps, int? ParentPid, int? Width, int? Height, double WarmupSeconds, LoadMode Mode = LoadMode.Borderless)
{
    public const string Usage =
        "Usage: Encore.Bench.GpuLoad [--target-fps 80] [--parent <pid>] [--size WxH] [--warmup 10] [--mode borderless|fullscreen|exclusive]";

    public const double DefaultTargetFps = 80;
    public const double DefaultWarmupSeconds = 10;

    public static LoadOptions Parse(IReadOnlyList<string> args)
    {
        double target = DefaultTargetFps, warmup = DefaultWarmupSeconds;
        int? parent = null, width = null, height = null;
        var mode = LoadMode.Borderless;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (!seen.Add(name))
            {
                throw new LoadUsageException($"{name} is given twice.");
            }

            if (i + 1 >= args.Count)
            {
                throw new LoadUsageException(name.StartsWith("--", StringComparison.Ordinal) ? $"{name} needs a value." : $"Unexpected argument '{name}'.");
            }

            var value = args[++i];
            switch (name)
            {
                case "--target-fps":
                    target = Number(name, value, 1, 1000);
                    break;
                case "--warmup":
                    warmup = Number(name, value, 1, 600);
                    break;
                case "--parent":
                    parent = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
                        ? pid
                        : throw new LoadUsageException($"--parent '{value}' is not a process id.");
                    break;
                case "--size":
                    var parts = value.Split('x');
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
                        || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                        || w is < 16 or > 16384 || h is < 16 or > 16384)
                    {
                        throw new LoadUsageException($"--size '{value}' is not WxH, each from 16 to 16384.");
                    }

                    (width, height) = (w, h);
                    break;
                case "--mode":
                    mode = value switch
                    {
                        "borderless" => LoadMode.Borderless,
                        "fullscreen" => LoadMode.Fullscreen,
                        "exclusive" => LoadMode.Exclusive,
                        _ => throw new LoadUsageException($"--mode '{value}' is not borderless, fullscreen or exclusive."),
                    };
                    break;
                default:
                    throw new LoadUsageException($"Unknown option '{name}'.");
            }
        }

        return new LoadOptions(target, parent, width, height, warmup, mode);
    }

    private static double Number(string name, string text, double min, double max) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new LoadUsageException($"{name} '{text}' is not a number from {min} to {max}.");
}
