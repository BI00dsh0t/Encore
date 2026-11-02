using System.Globalization;
using ReplayBuffer.Bench.GpuLoad;

// Story 1.14: Bench's synthetic GPU-bound load. A borderless popup over the primary monitor draws a
// shader whose cost is calibrated during warm-up to --target-fps, then held, so `bench fps` can measure
// the capture's FPS loss without a game. It prints one `ready {json}` line on stdout, then runs until
// Escape, WM_CLOSE, or the --parent process exits.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

LoadOptions options;
try
{
    options = LoadOptions.Parse(args);
}
catch (LoadUsageException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    Console.Error.WriteLine(LoadOptions.Usage);
    return LoadExit.Usage;
}

try
{
    using var load = GpuLoadWindow.Create(options);
    return load.Run();
}
catch (NoHardwareGpuException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return LoadExit.NoHardwareGpu;
}
catch (Exception e)
{
    Console.Error.WriteLine($"error: {e.GetType().Name}: {e.Message}");
    return LoadExit.Failed;
}
