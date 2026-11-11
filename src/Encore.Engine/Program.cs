using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Engine;
using Encore.Engine.Logging;
using Encore.Platform.Audio;
using Encore.Platform.Windows;

// Before instance resolution, mutex, logs or settings: this worker never touches the live engine.
if (args is ["clip-media-worker"])
{
    return await ClipMediaWorker.RunAsync();
}

var parsed = CommandLine.Parse(args);
var testInstance = parsed.Command switch
{
    ServeCommand serve => serve.Instance,
    DebugCaptureCommand capture => capture.Instance,
    _ => null,
};
var instance = testInstance is not null ? Instance.ForTest(testInstance) : Instance.Current;

// The rebrand (2026-11-10): an Encore instance's first start copies its old ReplayBuffer data once.
LegacyData.CopyIfFirstStart(instance);

// One engine per instance. Taken before anything else, and held until the process ends.
using var single = parsed.Command is ServeCommand ? SingleInstance.TryAcquire(SingleInstance.Engine, instance.Name) : null;
if (parsed.Command is ServeCommand && single is null)
{
    Console.Error.WriteLine($"error {ErrorCodes.InstanceRunning}: Another engine is already running for instance {instance.Name}.");
    return 1;
}
var logFolder = Path.Combine(instance.LocalDataFolder, "logs");
Directory.CreateDirectory(logFolder);

// Owned here, not by the factory, and declared first so it is disposed last and drains its queue.
using var logProvider = new TextLoggerProvider(logFolder, "engine");
using var loggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logProvider));
var log = loggers.CreateLogger("Engine");
log.LogInformation("Encore engine, instance {Instance}, pid {Pid}", instance.Name, Environment.ProcessId);

SetConsoleTitle(instance);
if (parsed.Command is null)
{
    Console.Error.WriteLine($"error {ErrorCodes.InvalidArguments}: {parsed.Error}");
    Console.Error.WriteLine(CommandLine.Usage);
    return 2;
}

try
{
    return parsed.Command switch
    {
        ServeCommand serve => await Serve(instance, serve, loggers, log),
        DebugListCommand => DebugList(),
        DebugCaptureCommand capture => RunDebugCapture(instance, capture, loggers),
        _ => 2,
    };
}
catch (Exception error)
{
    return ReportFailure(error, log);
}

// Keep this console's own title from matching a --window query.
static void SetConsoleTitle(Instance instance)
{
    try
    {
        Console.Title = $"{instance.Name} engine";
    }
    catch (IOException)
    {
    }
}

static CancellationTokenSource CancelOnCtrlC()
{
    var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, press) =>
    {
        press.Cancel = true;
        cancel.Cancel();
    };
    return cancel;
}

static async Task<int> Serve(Instance instance, ServeCommand serve, ILoggerFactory loggers, ILogger log)
{
    using var cancel = CancelOnCtrlC();
    await using var host = EngineHost.Start(instance, loggers, hostOptions: new EngineHostOptions
    {
        DebugCapture = serve.DebugCapture,
        Rewind = serve.Rewind ? true : null,
        Restarted = serve.Restarted,
    });

    // Tests wait for this line before connecting.
    Console.WriteLine("listening " + host.PipeName);
    await Task.WhenAny(host.ShutdownRequested, Task.Delay(Timeout.Infinite, cancel.Token));
    log.LogInformation("Shutting down: {Reason}", host.ShutdownRequested.IsCompleted ? "engine.shutdown" : "Ctrl+C");
    return 0;
}

static int RunDebugCapture(Instance instance, DebugCaptureCommand capture, ILoggerFactory loggers)
{
    using var cancel = CancelOnCtrlC();
    var path = new TracerCapture(capture, instance, loggers).Run(cancel.Token);
    Console.WriteLine(path);
    return 0;
}

static int DebugList()
{
    Console.WriteLine("Windows:");
    foreach (var window in WindowFinder.List())
    {
        Console.WriteLine("  " + DebugCaptureSources.Describe(window));
    }

    foreach (var flow in new[] { AudioFlow.Output, AudioFlow.Input })
    {
        var fallback = DefaultEndpoint(flow);
        Console.WriteLine();
        Console.WriteLine(flow == AudioFlow.Output ? "Output Devices (loopback):" : "Input Devices:");
        foreach (var endpoint in AudioEndpoints.List(flow))
        {
            var marker = endpoint.Id == fallback?.Id ? "* " : "  ";
            Console.WriteLine(marker + DebugCaptureSources.DescribeFully(endpoint));
        }
    }

    Console.WriteLine();
    Console.WriteLine("* = default (eConsole) device, used when no --output-device / --input-device is given.");
    return 0;
}

static AudioEndpoint? DefaultEndpoint(AudioFlow flow)
{
    try
    {
        return AudioEndpoints.Default(flow);
    }
    catch (Exception)
    {
        // No default endpoint for this flow.
        return null;
    }
}

// Prints the failure's code, message and candidates; an expected failure logs its trace at debug level only.
static int ReportFailure(Exception error, ILogger log)
{
    var (code, candidates) = error switch
    {
        EngineFailure failure => (failure.Code, failure.Candidates),
        ErrorCodeException coded => (coded.Code, (IReadOnlyList<string>)[]),
        _ => (ErrorCodes.CaptureFailed, (IReadOnlyList<string>)[]),
    };
    if (error is EngineFailure or ErrorCodeException)
    {
        log.LogDebug(error, "{Code}: {Message}", code, error.Message);
    }
    else
    {
        log.LogError(error, "{Code}: {Message}", code, error.Message);
    }

    Console.Error.WriteLine($"error {code}: {error.Message}");
    if (candidates.Count > 0)
    {
        Console.Error.WriteLine("Candidates:");
        foreach (var candidate in candidates)
        {
            Console.Error.WriteLine("  " + candidate);
        }
    }

    return 1;
}