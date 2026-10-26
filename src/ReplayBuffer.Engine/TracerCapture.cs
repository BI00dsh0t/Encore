using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Sessions;
using ReplayBuffer.Platform.Audio;
using ReplayBuffer.Platform.FFmpeg;
using ReplayBuffer.Platform.Windows;
using AudioFlow = ReplayBuffer.Platform.Audio.AudioFlow;
using SettingsAudioFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Engine;

/// <summary>A failure to report with its code, and the candidates the user can pick from.</summary>
public sealed class EngineFailure(string code, string message, IReadOnlyList<string>? candidates = null) : Exception(message)
{
    public string Code { get; } = code;

    public IReadOnlyList<string> Candidates { get; } = candidates ?? [];
}

/// <summary>
/// The <c>debug-capture</c> driver: resolves the window and devices from the command line, then
/// drives one session only through orchestrator messages: Start, wait (<c>--seconds</c> from the
/// anchor, Ctrl+C, or the session ending itself), End.
/// </summary>
public sealed class TracerCapture(DebugCaptureCommand command, Instance instance, ILoggerFactory loggers)
{
    private readonly ILogger _log = loggers.CreateLogger<TracerCapture>();
    private readonly long _qpcFrequency = Stopwatch.Frequency;

    /// <summary>Runs the capture and returns the path of the finished file.</summary>
    public string Run(CancellationToken cancellation)
    {
        FFmpegLibrary.Initialize(loggers.CreateLogger("FFmpeg"));

        var window = ResolveWindow();
        var output = ResolveEndpoint(AudioFlow.Output, command.OutputDevice);
        var input = ResolveEndpoint(AudioFlow.Input, command.InputDevice);
        _log.LogInformation("Window '{Title}' (0x{Handle:X}, {Process}); output '{Output}'; input '{Input}'",
            window.Title, window.Handle, window.ProcessName, output.FriendlyName, input.FriendlyName);

        var debugFolder = Path.Combine(instance.LocalDataFolder, "debug");
        var path = command.Output is { } explicitPath
            ? Path.GetFullPath(explicitPath)
            : OutputNaming.Allocate(debugFolder, window.Title, DateTime.Now, File.Exists);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string? statsPath = null;
        CaptureStatsCollector? stats = null;
        if (command.Stats is not null)
        {
            // Fail now, not after a long run, when the stats file can't be written.
            statsPath = Path.GetFullPath(command.Stats);
            try
            {
                CaptureStatsCollector.EnsureWritable(statsPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                throw new EngineFailure(ErrorCodes.StatsWriteFailed, $"Can't write the stats file '{statsPath}': {e.Message}");
            }

            stats = new CaptureStatsCollector(_qpcFrequency);
        }

        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new CaptureSessionOptions
        {
            OutputFolder = debugFolder,
            OutputPath = path,
            Stats = stats,
            StatsPath = statsPath,
            RequestedSeconds = command.Seconds,
            GpuPriority = command.GpuPriority,
            Stages = command.Stages,
            NvencPreset = command.NvencPreset,
            NvencTune = command.NvencTune,
            Color = command.Color,
            EncodeThread = command.EncodeThread,
            Anchored = qpc => anchored.TrySetResult(qpc),
        };

        // debug-capture doesn't open the settings store: the snapshot resolves against the defaults.
        using var orchestrator = new SessionOrchestrator(new CaptureSessionFactory(options, loggers), () => SettingsDocument.Empty, _ => { });
        var target = new CaptureTarget((int)window.ProcessId, window.Handle, ProfileId: null);
        var audio = new SessionAudio(
        [
            new SessionAudioDevice(output.Id, SettingsAudioFlow.Output, output.FriendlyName),
            new SessionAudioDevice(input.Id, SettingsAudioFlow.Input, input.FriendlyName),
        ]);

        var session = orchestrator.StartAsync(SessionKind.Game, target, audio).GetAwaiter().GetResult();
        var outcome = Wait(orchestrator, session, anchored.Task, stats, cancellation);
        if (outcome.Failure is { } failure)
        {
            throw failure as EngineFailure ?? new EngineFailure(CaptureSession.CodeOf(failure), failure.Message);
        }

        return outcome.Path!;
    }

    /// <summary>Waits for the session to run its course, then ends it, unless it ended itself first.</summary>
    private SessionOutcome Wait(SessionOrchestrator orchestrator, RunningSession session, Task<long> anchored, CaptureStatsCollector? stats, CancellationToken cancellation)
    {
        var ended = ((IAsyncResult)session.Ended).AsyncWaitHandle;
        var cancelled = cancellation.WaitHandle;

        // The session posts a failure itself when no first frame arrives in time.
        var first = WaitHandle.WaitAny([((IAsyncResult)anchored).AsyncWaitHandle, ended, cancelled]);
        if (first != 0)
        {
            return first == 1 ? session.Ended.GetAwaiter().GetResult() : End(orchestrator, session, "Ctrl+C");
        }

        // --seconds counts from the anchor, not from when the encoders finished opening.
        var anchorQpc = anchored.GetAwaiter().GetResult();
        if (stats is not null && command.Seconds > CaptureStatsCollector.WarmUp.TotalSeconds
            && WaitHandle.WaitAny([ended, cancelled], Remaining(anchorQpc, CaptureStatsCollector.WarmUp.TotalSeconds)) == WaitHandle.WaitTimeout)
        {
            stats.SnapshotWarmUpEnd();
        }

        return WaitHandle.WaitAny([ended, cancelled], Remaining(anchorQpc, command.Seconds)) switch
        {
            0 => session.Ended.GetAwaiter().GetResult(),
            1 => End(orchestrator, session, "Ctrl+C"),
            _ => End(orchestrator, session, $"{command.Seconds} s elapsed"),
        };
    }

    private static SessionOutcome End(SessionOrchestrator orchestrator, RunningSession session, string reason)
    {
        try
        {
            return orchestrator.EndAsync(reason).GetAwaiter().GetResult();
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.SessionNotRunning)
        {
            // It ended itself (window closed, or a failure) just before.
            return session.Ended.GetAwaiter().GetResult();
        }
    }

    private TimeSpan Remaining(long anchorQpc, double secondsFromAnchor)
    {
        var elapsed = (double)(Stopwatch.GetTimestamp() - anchorQpc) / _qpcFrequency;
        return TimeSpan.FromSeconds(Math.Max(0, secondsFromAnchor - elapsed));
    }

    private WindowInfo ResolveWindow()
    {
        if (command.Hwnd is { } handle)
        {
            return WindowFinder.FromHandle(handle)
                ?? throw new EngineFailure(ErrorCodes.WindowNotFound, $"0x{handle:X} is not a visible, titled top-level window.",
                    WindowFinder.List().Select(Describe).ToList());
        }

        var match = WindowFinder.Match(WindowFinder.List(), command.Window!);
        return match.Outcome switch
        {
            WindowMatchOutcome.Found => match.Window!,
            WindowMatchOutcome.NotFound => throw new EngineFailure(ErrorCodes.WindowNotFound,
                $"No window title contains '{command.Window}'.", match.Candidates.Select(Describe).ToList()),
            _ => throw new EngineFailure(ErrorCodes.WindowAmbiguous,
                $"{match.Candidates.Count} window titles contain '{command.Window}'; use a longer title or --hwnd.",
                match.Candidates.Select(Describe).ToList()),
        };
    }

    private static AudioEndpoint ResolveEndpoint(AudioFlow flow, string? query)
    {
        try
        {
            if (query is null)
            {
                return AudioEndpoints.Default(flow);
            }

            var endpoints = AudioEndpoints.List(flow);
            var match = AudioEndpoints.Match(endpoints, query);
            var kind = flow == AudioFlow.Output ? "output" : "input";
            return match.Outcome switch
            {
                EndpointMatchOutcome.Found => match.Endpoint!,
                EndpointMatchOutcome.NotFound => throw new EngineFailure(ErrorCodes.AudioDeviceNotFound,
                    $"No {kind} device name contains '{query}'.", match.Candidates.Select(Describe).ToList()),
                _ => throw new EngineFailure(ErrorCodes.AudioDeviceAmbiguous,
                    $"{match.Candidates.Count} {kind} device names contain '{query}'; use the full name.",
                    match.Candidates.Select(Describe).ToList()),
            };
        }
        catch (Exception e) when (e is not EngineFailure)
        {
            throw new EngineFailure(ErrorCodes.AudioDeviceFailed, $"Could not list {flow} devices: {e.Message}");
        }
    }

    internal static string Describe(WindowInfo w) => $"0x{w.Handle:X8}  {w.ProcessName ?? "?",-24} {w.Title}";

    internal static string Describe(AudioEndpoint e) => $"{e.FriendlyName}  [{e.Id}]";
}
