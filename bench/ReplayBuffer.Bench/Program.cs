using System.Globalization;
using ReplayBuffer.Bench;

// Story 1.14: the FPS, drift, playback and Hotkey benchmarks of R4, R5, NFR-4 and R11, and story 2.9's
// Named Tracks check of R3 and R7, and story 5.2's Overlay measurement, each into one run folder, merged into report.md and report.json after
// every measurement.

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

// Like measure-path-a.ps1: the engine must run as it will in use (AD-7), so never elevated.
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
        _ => 0,
    };

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
catch (ReplayBuffer.Contracts.ErrorCodeException e)
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
