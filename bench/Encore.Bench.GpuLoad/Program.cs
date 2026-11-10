using System.Globalization;
using Encore.Bench.GpuLoad;

// Bench's synthetic GPU load: prints one ready line once calibrated, then runs until Escape, close or its parent exits.

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
