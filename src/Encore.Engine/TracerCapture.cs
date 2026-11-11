using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Settings;
using Encore.Core.Sessions;
using Encore.Engine.Sessions;
using Encore.Engine.Settings;
using Encore.Platform.Audio;
using Encore.Platform.Capture;
using Encore.Platform.FFmpeg;
using Encore.Platform.Windows;

namespace Encore.Engine;

/// <summary>The <c>debug-capture</c> driver: sets one session up from the command line, then runs it to its end.</summary>
public sealed class TracerCapture(DebugCaptureCommand command, Instance instance, ILoggerFactory loggers)
{
    private readonly ILogger _log = loggers.CreateLogger<TracerCapture>();
    private readonly long _ticksPerSecond = Stopwatch.Frequency;

    /// <summary>Runs the capture and returns the path of the finished file.</summary>
    public string Run(CancellationToken cancellation)
    {
        FFmpegLibrary.Initialize(loggers.CreateLogger("FFmpeg"));

        // With --track-layout the stored settings come first: a bad layout fails before any capture.
        var stored = command.TrackLayout ? SettingsStore.ReadDocument(instance.RoamingDataFolder) : SettingsDocument.Empty;
        var audio = command.TrackLayout ? DebugCaptureSources.LayoutAudio(stored) : null;

        // The monitors option captures the composited monitors, with no window.
        var window = command.Monitors is null ? DebugCaptureSources.ResolveWindow(command) : null;
        audio ??= DebugCaptureSources.DeviceAudio(command);
        LogSources(window, audio);

        var debugFolder = Path.Combine(instance.LocalDataFolder, "debug");
        var path = command.Output is { } explicitPath
            ? Path.GetFullPath(explicitPath)
            : OutputNaming.Allocate(debugFolder, window?.Title ?? "Monitors", DateTime.Now, File.Exists);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var (stats, statsPath) = PrepareStats();
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
            Anchored = timestamp => anchored.TrySetResult(timestamp),
        };

        // The settings store isn't opened here: the snapshot is the stored document with the command line's video value.
        var document = stored with { Global = stored.Global.SetItem(Keys.Video.Name, Keys.Video.ToJson(VideoFor(window))) };
        return Capture(options, document, window, audio, anchored.Task, stats, cancellation);
    }

    /// <summary>Runs one session to its end and returns its file; a failed session throws its code.</summary>
    private string Capture(
        CaptureSessionOptions options,
        SettingsDocument document,
        WindowInfo? window,
        SessionAudio audio,
        Task<long> anchored,
        CaptureStatsCollector? stats,
        CancellationToken cancellation)
    {
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
        var outcome = Wait(orchestrator, session, anchored, stats, cancellation);
        if (outcome.Failure is { } failure)
        {
            throw failure as EngineFailure ?? new EngineFailure(EngineFailure.CodeOf(failure), failure.Message);
        }

        return outcome.Path!;
    }

    /// <summary>The stats collector and its full path when <c>--stats</c> is given; fails now when the file can't be written.</summary>
    private (CaptureStatsCollector? Stats, string? Path) PrepareStats()
    {
        if (command.Stats is null)
        {
            return (null, null);
        }

        // Fail now, not after a long run, when the stats file can't be written.
        var statsPath = Path.GetFullPath(command.Stats);
        try
        {
            CaptureStatsCollector.EnsureWritable(statsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new EngineFailure(ErrorCodes.StatsWriteFailed, $"Can't write the stats file '{statsPath}': {e.Message}");
        }

        return (new CaptureStatsCollector(_ticksPerSecond), statsPath);
    }

    private void LogSources(WindowInfo? window, SessionAudio audio)
    {
        var tracks = audio.Tracks.Select(track =>
            $"'{track.Name}' = {string.Join(" + ", track.DeviceIndexes.Select(index => $"'{audio.Devices[index].FriendlyName}'"))}");
        _log.LogInformation(
            "Window '{Title}' (0x{Handle:X}, {Process}); Tracks {Tracks}",
            window?.Title ?? "(monitors)",
            window?.Handle ?? 0,
            window?.ProcessName,
            string.Join(", ", tracks));
    }

    /// <summary>Says which Mic Device inputs the session's starting mic mute silences, if any.</summary>
    private void LogMicMute(SessionSpec spec)
    {
        var silenced = spec.Audio.GatedMicInputs()
            .Select(input => $"'{input.Device.FriendlyName}' in '{input.Track.Name}'")
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

    /// <summary>The session's <c>video</c> value from the command line, sized to the window (made even) when no size is given.</summary>
    private VideoSettings VideoFor(WindowInfo? window)
    {
        var (width, height) = command.Size is { } size
            ? (size.Width, size.Height)
            : window is null ? MonitorCanvasSize() : WindowSize(window);

        // --match-source: the chain takes the window's own size at its first frame; width and height are the fallback.
        var video = new VideoSettings(command.Codec, width, height, command.Fps, command.BitrateKbps, RateControl.Cbr, command.MatchSource);
        _log.LogInformation(
            "Video: {Codec} {Width}x{Height} at {Fps} fps, CBR {Kbps} kbps{Forced}",
            video.Codec,
            width,
            height,
            video.Fps,
            video.BitrateKbps,
            command.Encoder is { } forced ? $", encoder forced to {forced}" : "");
        return video;
    }

    /// <summary>The canvas rule, fitted to the codec's largest side only (no pixel budget).</summary>
    private (int Width, int Height) MonitorCanvasSize()
    {
        var placements = MonitorCanvas.Place(command.Monitors!);
        if (placements.Count == 0)
        {
            var names = string.Join(", ", command.Monitors!);
            throw new EngineFailure(ErrorCodes.CaptureFailed, $"None of the monitors '{names}' is attached.");
        }

        var native = MonitorCanvas.Bounds(placements).Size;
        var maxSide = Core.Monitors.MonitorCanvasRule.MaxSide(command.Codec);
        var fitted = MonitorCanvas.Fit(native, maxSide);
        _log.LogInformation("Monitors {Monitors}: canvas {Native}, encoded at {Fitted}", string.Join("; ", placements), native, fitted);
        return (fitted.Width, fitted.Height);
    }

    private static (int Width, int Height) WindowSize(WindowInfo window)
    {
        var size = WindowCapture.SizeOf(window.Handle);
        var even = VideoSize.EvenFloor(Math.Min(size.Width, VideoSettings.MaxDimension), Math.Min(size.Height, VideoSettings.MaxDimension));
        return (even.Width, even.Height);
    }

    /// <summary>Waits for the session to run its course, then ends it, unless it ended itself first.</summary>
    private SessionOutcome Wait(
        SessionOrchestrator orchestrator,
        RunningSession session,
        Task<long> anchored,
        CaptureStatsCollector? stats,
        CancellationToken cancellation)
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
        var anchor = anchored.GetAwaiter().GetResult();
        var warmUp = CaptureStatsCollector.WarmUp.TotalSeconds;
        if (stats is not null && command.Seconds > warmUp
            && WaitHandle.WaitAny([ended, cancelled], Remaining(anchor, warmUp)) == WaitHandle.WaitTimeout)
        {
            stats.SnapshotWarmUpEnd();
        }

        return WaitHandle.WaitAny([ended, cancelled], Remaining(anchor, command.Seconds)) switch
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

    /// <summary>The time left until secondsFromAnchor seconds after the anchor's timestamp.</summary>
    private TimeSpan Remaining(long anchor, double secondsFromAnchor)
    {
        var elapsed = (double)(Stopwatch.GetTimestamp() - anchor) / _ticksPerSecond;
        return TimeSpan.FromSeconds(Math.Max(0, secondsFromAnchor - elapsed));
    }
}
