using System.Globalization;
using Encore.Bench;

// Bench: the FPS, drift, playback, Hotkey, Tracks and Overlay measurements, each into one run folder with a report.

// One number format on every locale, for report.md and every message, on every thread.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(BenchOptions.Usage);
    return args.Length == 0 ? 2 : 0;
}

BenchOptions options;
try
{
    options = BenchOptions.Parse(args);
}
catch (BenchUsageException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    Console.Error.WriteLine(BenchOptions.Usage);
    return 2;
}

// Like measure-path-a.ps1: the engine must run as it will in use, so never elevated.
if (BenchContext.IsElevated())
{
    Console.Error.WriteLine("This terminal is elevated. Run Bench unelevated: the engine must run as it will in use (AD-7). Join \"Performance Log Users\" so PresentMon can trace without elevation.");
    return 1;
}

try
{
    var context = BenchContext.Create(options);
    var code = options.Command switch
    {
        "fps" => FpsBenchmark.Run(context),
        "drift" => DriftBenchmark.Run(context),
        "playback" => PlaybackCheck.Run(context),
        "hotkey" => HotkeyBenchmark.Run(context),
        "tracks" => TracksCheck.Run(context),
        "overlay" => OverlayBenchmark.Run(context),
        "soak" => SoakRun.Run(context),
        _ => 0,
    };

    // The soak writes its own soak.md; report.md merges the other measurements only.
    if (options.Command == "soak")
    {
        return code;
    }

    Report.Generate(context.RunFolder);
    Console.WriteLine($"Report: {Path.Combine(context.RunFolder, Report.MarkdownName)}");
    return code;
}
catch (BenchUsageException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    Console.Error.WriteLine(BenchOptions.Usage);
    return 2;
}
catch (BenchFailure e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}
catch (Encore.Contracts.ErrorCodeException e)
{
    Console.Error.WriteLine($"error {e.Code}: {e.Message}");
    return 1;
}
catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException)
{
    // A process or pipe that didn't answer in time, Ctrl+C, or a file that couldn't be written: exit 1, not a crash.
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}
