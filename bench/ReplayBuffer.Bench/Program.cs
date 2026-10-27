using ReplayBuffer.Bench;

// Story 1.14: the FPS, drift, playback and Hotkey benchmarks of R4, R5, NFR-4 and R11, each into one
// run folder, merged into report.md and report.json after every measurement.
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
