using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Core.Settings;
using ReplayBuffer.Engine.Sessions;
using ReplayBuffer.Engine.Settings;
using ReplayBuffer.Platform.Audio;
using ReplayBuffer.Platform.Capture;
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
/// anchor, Ctrl+C, or the session ending itself), End. The session starts with the instance's saved
/// mic mute (<c>runtime.json</c>, muted when there is none); with no Hotkeys it never changes, so a
/// muted run silences each Mic Device in the Track Layout's <c>muteAffected</c> Tracks.
/// </summary>
public sealed class TracerCapture(DebugCaptureCommand command, Instance instance, ILoggerFactory loggers)
{
    private readonly ILogger _log = loggers.CreateLogger<TracerCapture>();
    private readonly long _qpcFrequency = Stopwatch.Frequency;

    /// <summary>Runs the capture and returns the path of the finished file.</summary>
    public string Run(CancellationToken cancellation)
    {
        FFmpegLibrary.Initialize(loggers.CreateLogger("FFmpeg"));

        // With --track-layout the stored settings come first: a bad layout fails before any capture.
        var stored = command.TrackLayout ? SettingsStore.ReadDocument(instance.RoamingDataFolder) : SettingsDocument.Empty;
        var audio = command.TrackLayout ? LayoutAudio(stored) : null;

        // Spike 11.1: --monitors captures the composited monitors, with no window.
        var window = command.Monitors is null ? ResolveWindow() : null;
        if (audio is null)
        {
            var output = ResolveEndpoint(AudioFlow.Output, command.OutputDevice);
            var input = ResolveEndpoint(AudioFlow.Input, command.InputDevice);
            audio = SessionAudio.OnePerDevice(
            [
                new SessionAudioDevice(output.Id, SettingsAudioFlow.Output, output.FriendlyName),
                new SessionAudioDevice(input.Id, SettingsAudioFlow.Input, input.FriendlyName),
            ]);
        }

        _log.LogInformation("Window '{Title}' (0x{Handle:X}, {Process}); Tracks {Tracks}",
            window?.Title ?? "(monitors)", window?.Handle ?? 0, window?.ProcessName,
            string.Join(", ", audio.Tracks.Select(t => $"'{t.Name}' = {string.Join(" + ", t.DeviceIndexes.Select(i => $"'{audio.Devices[i].FriendlyName}'"))}")));

        var debugFolder = Path.Combine(instance.LocalDataFolder, "debug");
        var path = command.Output is { } explicitPath
            ? Path.GetFullPath(explicitPath)
            : OutputNaming.Allocate(debugFolder, window?.Title ?? "Monitors", DateTime.Now, File.Exists);
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
            Encoder = command.Encoder,
            CompositeMonitors = command.Monitors,
            CompositeDuplication = command.MonitorDuplication,
            Anchored = qpc => anchored.TrySetResult(qpc),
        };

        // debug-capture doesn't open the settings store: the snapshot resolves against the document
        // read with --track-layout (else the empty one) with the command line's video value set.
        var document = stored with { Global = stored.Global.SetItem(Keys.Video.Name, Keys.Video.ToJson(VideoFor(window))) };

        // One hub for the run, disposed after the orchestrator has ended the session.
        using var hub = new AudioDeviceHub(loggers);
        using var orchestrator = new SessionOrchestrator(
            new CaptureSessionFactory(options, loggers, hub),
            () => document,
            _ => { },
            micMute: new RuntimeStateStore(instance.LocalDataFolder, loggers.CreateLogger("Audio")));
        var target = window is null
            ? new CaptureTarget(Environment.ProcessId, 0, ProfileId: null)
            : new CaptureTarget((int)window.ProcessId, window.Handle, ProfileId: null);
        var session = orchestrator.StartAsync(SessionKind.Game, target, audio).GetAwaiter().GetResult();
        LogMicMute(session.Spec);
        var outcome = Wait(orchestrator, session, anchored.Task, stats, cancellation);
        if (outcome.Failure is { } failure)
        {
            throw failure as EngineFailure ?? new EngineFailure(CaptureSession.CodeOf(failure), failure.Message);
        }

        return outcome.Path!;
    }

    /// <summary>
    /// The session audio of the stored Global <c>trackLayout</c>: refused when the stored value is
    /// invalid, or when a device is not present with its flow. A Track with no devices is a silent
    /// Track (<see cref="SessionAudio.FromLayout"/>).
    /// </summary>
    private SessionAudio LayoutAudio(SettingsDocument document)
    {
        if (document.Global.TryGetValue(Keys.TrackLayout.Name, out var raw) && !Keys.TrackLayout.IsValid(raw))
        {
            throw new EngineFailure(ErrorCodes.SettingInvalidValue,
                $"The stored '{Keys.TrackLayout.Name}' is not a valid Track Layout; fix or remove it in {SettingsStore.FileName}.");
        }

        var layout = SettingsResolver.Resolve(document, null).TrackLayout;
        var audio = SessionAudio.FromLayout(layout);

        var present = new Dictionary<SettingsAudioFlow, IReadOnlyList<AudioEndpoint>>();
        foreach (var device in audio.Devices)
        {
            var flow = device.Flow == SettingsAudioFlow.Output ? AudioFlow.Output : AudioFlow.Input;
            if (!present.TryGetValue(device.Flow, out var endpoints))
            {
                try
                {
                    endpoints = AudioEndpoints.List(flow);
                }
                catch (Exception e)
                {
                    throw new EngineFailure(ErrorCodes.AudioDeviceFailed, $"Could not list {flow} devices: {e.Message}");
                }

                present[device.Flow] = endpoints;
            }

            if (!endpoints.Any(e => string.Equals(e.Id, device.EndpointId, StringComparison.OrdinalIgnoreCase)))
            {
                var kind = flow == AudioFlow.Output ? "output" : "input";
                throw new EngineFailure(ErrorCodes.AudioDeviceNotFound,
                    $"The Track Layout's {kind} device '{device.FriendlyName}' [{device.EndpointId}] is not present.",
                    endpoints.Select(Describe).ToList());
            }
        }

        return audio;
    }

    /// <summary>Says which Mic Device inputs the session's starting mic mute silences, if any.</summary>
    private void LogMicMute(SessionSpec spec)
    {
        var silenced = spec.Audio.GatedMicInputs()
            .Select(p => $"'{p.Device.FriendlyName}' in '{p.Track.Name}'")
            .ToList();
        if (!spec.MicMuted)
        {
            _log.LogInformation("Mic unmuted (runtime.json): every Mic Device is recorded");
        }
        else if (silenced.Count == 0)
        {
            _log.LogInformation("Mic muted, but no Mic Device is in a muteAffected Track: nothing is silenced");
        }
        else
        {
            _log.LogInformation("Mic muted (runtime.json missing or muted): silenced {Inputs}", string.Join(", ", silenced));
        }
    }

    /// <summary>
    /// The session's <c>video</c> value from the command line, sized to the window (made even) when
    /// no <c>--size</c> is given.
    /// </summary>
    private VideoSettings VideoFor(WindowInfo? window)
    {
        int width, height;
        if (command.Size is { } size)
        {
            (width, height) = (size.Width, size.Height);
        }
        else if (window is null)
        {
            // Spike 11.1: the canvas rule, fitted to the codec's largest side only (no pixel budget).
            var placements = MonitorCanvas.Place(command.Monitors!);
            if (placements.Count == 0)
            {
                throw new EngineFailure(ErrorCodes.CaptureFailed, $"None of the monitors '{string.Join(", ", command.Monitors!)}' is attached.");
            }

            var native = MonitorCanvas.Bounds(placements).Size;
            var maxSide = Core.Monitors.MonitorCanvasRule.MaxSide(command.Codec);
            var fitted = MonitorCanvas.Fit(native, maxSide);
            (width, height) = (fitted.Width, fitted.Height);
            _log.LogInformation("Monitors {Monitors}: canvas {Native}, encoded at {Fitted}", string.Join("; ", placements), native, fitted);
        }
        else
        {
            var item = WindowCapture.SizeOf(window.Handle);
            var even = VideoSize.EvenFloor(Math.Min(item.Width, VideoSettings.MaxDimension), Math.Min(item.Height, VideoSettings.MaxDimension));
            (width, height) = (even.Width, even.Height);
        }

        var video = new VideoSettings(command.Codec, width, height, command.Fps, command.BitrateKbps, RateControl.Cbr);
        _log.LogInformation("Video: {Codec} {Width}x{Height} at {Fps} fps, CBR {Kbps} kbps{Forced}",
            video.Codec, width, height, video.Fps, video.BitrateKbps, command.Encoder is { } forced ? $", encoder forced to {forced}" : "");
        return video;
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

    /// <summary><c>debug-list</c>'s line: flow, friendly name, parent name and endpoint id.</summary>
    internal static string DescribeFully(AudioEndpoint e) =>
        $"{(e.Flow == AudioFlow.Output ? "output" : "input"),-6}  {e.FriendlyName}  parent: {(e.ParentName.Length > 0 ? e.ParentName : "-")}  [{e.Id}]";
}
