using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Engine;
using ReplayBuffer.Engine.Logging;
using ReplayBuffer.Platform.Audio;
using ReplayBuffer.Platform.Windows;

var parsed = CommandLine.Parse(args);
var instance = parsed.Command is ServeCommand { Instance: { } testInstance }
    ? new Instance(
        testInstance,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), testInstance),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), testInstance))
    : Instance.Current;
var logFolder = Path.Combine(instance.LocalDataFolder, "logs");
Directory.CreateDirectory(logFolder);

// Owned here, not by the factory (which never disposes providers added by instance), and
// declared first so it is disposed last and drains its queue.
using var logProvider = new TextLoggerProvider(logFolder, "engine");
using var loggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logProvider));
var log = loggers.CreateLogger("Engine");
log.LogInformation("ReplayBuffer engine, instance {Instance}, pid {Pid}", instance.Name, Environment.ProcessId);

// Keep this console's own title from matching a --window query.
try
{
    Console.Title = $"{instance.Name} engine";
}
catch (IOException)
{
}

if (parsed.Command is null)
{
    Console.Error.WriteLine($"error {ErrorCodes.InvalidArguments}: {parsed.Error}");
    Console.Error.WriteLine(CommandLine.Usage);
    return 2;
}

try
{
    switch (parsed.Command)
    {
        case ServeCommand:
            return await Serve(instance, loggers, log);

        case DebugListCommand:
            DebugList();
            return 0;

        case DebugCaptureCommand capture:
            using (var cancel = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cancel.Cancel();
                };

                var path = new TracerCapture(capture, instance, loggers).Run(cancel.Token);
                Console.WriteLine(path);
                return 0;
            }

        default:
            return 2;
    }
}
catch (Exception e)
{
    var (code, candidates) = e switch
    {
        EngineFailure f => (f.Code, f.Candidates),
        ErrorCodeException c => (c.Code, (IReadOnlyList<string>)[]),
        _ => (ErrorCodes.CaptureFailed, (IReadOnlyList<string>)[]),
    };
    if (e is EngineFailure or ErrorCodeException)
    {
        // An expected failure: the code and message are enough on the console; the trace goes to the file.
        log.LogDebug(e, "{Code}: {Message}", code, e.Message);
    }
    else
    {
        log.LogError(e, "{Code}: {Message}", code, e.Message);
    }

    Console.Error.WriteLine($"error {code}: {e.Message}");
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

static async Task<int> Serve(Instance instance, ILoggerFactory loggers, ILogger log)
{
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };

    await using var host = EngineHost.Start(instance, loggers);

    // Tests wait for this line before connecting.
    Console.WriteLine("listening " + host.PipeName);
    await Task.WhenAny(host.ShutdownRequested, Task.Delay(Timeout.Infinite, cancel.Token));
    log.LogInformation("Shutting down: {Reason}", host.ShutdownRequested.IsCompleted ? "engine.shutdown" : "Ctrl+C");
    return 0;
}

static void DebugList()
{
    Console.WriteLine("Windows:");
    foreach (var window in WindowFinder.List())
    {
        Console.WriteLine("  " + TracerCapture.Describe(window));
    }

    foreach (var flow in new[] { AudioFlow.Output, AudioFlow.Input })
    {
        AudioEndpoint? fallback = null;
        try
        {
            fallback = AudioEndpoints.Default(flow);
        }
        catch (Exception)
        {
            // No default endpoint for this flow.
        }

        Console.WriteLine();
        Console.WriteLine(flow == AudioFlow.Output ? "Output Devices (loopback):" : "Input Devices:");
        foreach (var endpoint in AudioEndpoints.List(flow))
        {
            var marker = endpoint.Id == fallback?.Id ? "* " : "  ";
            Console.WriteLine(marker + TracerCapture.Describe(endpoint));
        }
    }

    Console.WriteLine();
    Console.WriteLine("* = default (eConsole) device, used when no --output-device / --input-device is given.");
}
