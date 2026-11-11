using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Encore.Bench;

/// <summary>One phase of one mode: the dot hidden, shown, or hidden again, and what PresentMon and the shell saw.</summary>
internal sealed record OverlayPhase(
    string Mode,
    string Phase,
    bool DotShown,
    int Frames,
    double? AvgFps,
    double? OnePercentLowFps,
    string? PresentMode,
    IReadOnlyDictionary<string, double> PresentModePercent,
    int NotificationState,
    string NotificationStateName,
    bool LoadInForeground,
    bool OverlayWindowVisible,
    bool OverlayOnScreen,
    string? Error);

/// <summary>A mode's summary: the dot's FPS cost against the mean of the two hidden phases, and its presentation path.</summary>
internal sealed record OverlayModeSummary(
    string Mode,
    string? PresentModeOff,
    string? PresentModeOn,
    string? PresentModeOffAgain,
    double? FpsOff,
    double? FpsOn,
    double? FpsCostPercent,
    bool IndependentFlipKept,
    bool OverlayShows,
    bool FullscreenD3D,
    IReadOnlyList<string> FullscreenChanges,
    bool ExclusiveKept);

/// <summary>The <c>overlay.json</c> report; OverlayPlanes is per load, null when DXGI couldn't say.</summary>
internal sealed record OverlayReport(
    DateTimeOffset Created,
    string Host,
    bool RemoteSession,
    int Seconds,
    int Settle,
    IReadOnlyList<GpuLoadInfo> Loads,
    OverlayHostReady? Overlay,
    IReadOnlyList<OverlayPhase> Phases,
    IReadOnlyList<OverlayModeSummary> Summaries,
    string? Aborted,
    string? OverlayDot = null,
    IReadOnlyList<bool?>? OverlayPlanes = null);

/// <summary><c>bench overlay</c>: measures the load's presents with the Overlay's dot hidden, shown and hidden again.</summary>
internal static class OverlayBenchmark
{
    public const string FileName = "overlay.json";
    public const string MarkdownName = "overlay.md";
    public const string RemoteLabel = "measured over RDP: not representative";
    public static readonly string[] Modes = ["borderless", "fullscreen", "exclusive"];
    public static readonly string[] Hosts = ["stand-in", "app"];

    /// <summary>The modes in which the GPU load's frames reach the screen without DWM: the dot can't be seen there.</summary>
    private static readonly string[] Exclusive = ["Hardware: Legacy Flip", "Hardware: Legacy Copy to front buffer"];

    private const int QunsRunningD3DFullScreen = 3;
    private const string LoadNotePrefix = "load: ";

    // With the App as host, each message shows for 4 s, so the phases must outlast the message queue.
    private const int AppMinSettle = 5;
    private const int AppMinSeconds = 10;

    public static int Run(BenchContext context, TextWriter? errors = null)
    {
        errors ??= Console.Error;
        var options = context.Options;
        var modes = options.List("modes", Modes);
        if (modes.FirstOrDefault(m => !Modes.Contains(m)) is { } unknown)
        {
            throw new BenchUsageException($"--modes '{unknown}' is not one of {string.Join(", ", Modes)}.");
        }

        var host = options.Text("host", "stand-in");
        if (!Hosts.Contains(host))
        {
            throw new BenchUsageException($"--host '{host}' is not one of {string.Join(", ", Hosts)}.");
        }

        var app = host == "app";
        if (app && options.Has("overlay-exe"))
        {
            throw new BenchUsageException("--overlay-exe is the stand-in's; --host app runs the App (--app-exe).");
        }

        if (!app && options.Flag("dot-only"))
        {
            throw new BenchUsageException("--dot-only is for --host app; the stand-in shows only the dot.");
        }

        if (!app && options.Has("app-exe"))
        {
            throw new BenchUsageException("--app-exe is for --host app.");
        }

        if (!app && options.Has("overlay-dot"))
        {
            throw new BenchUsageException("--overlay-dot is for --host app; the stand-in has no overlayDot setting.");
        }

        var dot = app ? AppOverlayHost.ParseDot(options) : (Contracts.Settings.OverlayDotMode?)null;

        // Three phases of 5 s settling and 15 s of presents: a minute per mode.
        var seconds = options.Int("seconds", 15, 5, 600);
        var settle = options.Int("settle", 5, 0, 120);
        if (app && (settle < AppMinSettle || seconds < AppMinSeconds))
        {
            throw new BenchUsageException($"--host app needs --settle {AppMinSettle} and --seconds {AppMinSeconds} or more, so its 4 s messages have gone before a hidden phase.");
        }
        var targetFps = options.Double("target-fps", GpuLoadProcess.DefaultTargetFps, 1, 1000);
        var loadSize = options.Text("load-size");
        if (loadSize is not null)
        {
            BenchOptions.ParseSize(loadSize, "load-size");
        }

        var loadWarmup = options.Has("load-warmup") ? options.Double("load-warmup", 10, 1, 600) : (double?)null;
        var loadTimeout = TimeSpan.FromSeconds(Math.Max(options.Int("load-timeout", 60, 5, 600), (loadWarmup ?? 10) + 30));
        var presentMon = context.ResolvePresentMon();
        var loadExe = GpuLoadProcess.Resolve(context);
        var overlayExe = app ? AppOverlayHost.Resolve(context) : OverlayHostProcess.Resolve(context);
        var folder = Path.Combine(context.RunFolder, "overlay");
        Directory.CreateDirectory(folder);
        var remote = IsRemoteSession();
        if (remote)
        {
            errors.WriteLine($"This is a Remote Desktop session: every figure is {RemoteLabel}.");
        }

        var phases = new List<OverlayPhase>();
        var summaries = new List<OverlayModeSummary>();
        var loads = new List<GpuLoadInfo>();
        var planes = new List<bool?>();
        OverlayHostReady? overlayReady = null;
        string? aborted = null;

        // The App and its engine outlive the loads; the stand-in is started beside each load.
        using var appHost = app ? AppOverlayHost.Start(overlayExe, loadExe, messages: !options.Flag("dot-only"), dot) : null;
        foreach (var mode in modes)
        {
            if (GpuLoadProcess.Running() is { Count: > 0 } running)
            {
                throw new BenchFailure($"{GpuLoadProcess.ProcessName} is already running (pid {string.Join(", ", running)}); close it before measuring.");
            }

            Console.WriteLine($"[{mode}] starting the GPU load ...");
            GpuLoadProcess load;
            try
            {
                load = GpuLoadProcess.Start(loadExe, targetFps, loadSize, loadWarmup, loadTimeout, mode);
            }
            catch (BenchFailure e)
            {
                aborted = Join(aborted, $"{mode}: {e.Message}");
                errors.WriteLine($"{mode}: {e.Message}");
                continue;
            }

            using (load)
            {
                loads.Add(load.Info);
                planes.Add(Encore.Platform.Desktop.Monitors.OverlayPlanes(load.Id));
                using var standIn = app ? null : OverlayHostProcess.Start(overlayExe, load.Id);
                overlayReady ??= standIn?.Ready;
                IOverlayHost overlay = (IOverlayHost?)appHost ?? standIn!;
                var modePhases = new List<OverlayPhase>();
                foreach (var (phase, shown) in new[] { ("off", false), ("on", true), ("off-again", false) })
                {
                    // A game is in front while it is played: bring the load back if the desktop took the foreground.
                    BringToFront(load.Hwnd);
                    if (shown)
                    {
                        overlay.On();
                    }
                    else
                    {
                        overlay.Off();
                    }

                    Console.WriteLine($"[{mode}/{phase}] settling {settle} s, then PresentMon {seconds} s ...");
                    Thread.Sleep(TimeSpan.FromSeconds(settle));
                    var state = NotificationState();
                    var inFrontBefore = Encore.Platform.Desktop.OverlayWindowStyle.ForegroundWindow() == load.Hwnd;
                    var visible = overlay.WindowVisible();
                    var csv = Path.Combine(folder, $"{mode}-{phase}-presentmon.csv");
                    var error = RecordPresents(presentMon, load.Info.Process + ".exe", csv, seconds, folder, $"{mode}-{phase}");

                    // In front for the whole phase: before and after PresentMon's window.
                    var foreground = inFrontBefore && Encore.Platform.Desktop.OverlayWindowStyle.ForegroundWindow() == load.Hwnd;
                    if (load.HasExited)
                    {
                        error = $"the GPU load exited ({load.ExitDescription()})";
                    }

                    var presents = PresentMonCsv.Analyze(csv);
                    var row = new OverlayPhase(
                        mode, phase, shown, presents.Frames, presents.AvgFps, presents.OnePercentLowFps, presents.DominantMode, presents.ModePercent,
                        state, NotificationStateName(state), foreground, visible, OnScreen(visible, presents.DominantMode), error ?? presents.Error);
                    Console.WriteLine($"[{mode}/{phase}] {Num(row.AvgFps)} fps, {row.PresentMode ?? "no present mode"}, {row.NotificationStateName}, foreground {foreground}, dot visible {visible}.");
                    modePhases.Add(row);
                    if (load.HasExited)
                    {
                        break;
                    }
                }

                phases.AddRange(modePhases);
                // The fullscreen changes after ready, and the load's own notes on refused or rebuilt swap chains.
                var changes = load.FullscreenChanges
                    .Concat(load.Stderr.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(l => LoadNotePrefix + l))
                    .ToList();
                summaries.Add(Summarize(mode, modePhases, changes));
                if (load.HasExited)
                {
                    aborted = Join(aborted, $"{mode}: the GPU load exited during the run");
                }
            }
        }

        var report = new OverlayReport(DateTimeOffset.Now, host, remote, seconds, settle, loads, overlayReady, phases, summaries, aborted,
            dot is { } d ? AppOverlayHost.DotName(d) : null, planes);
        BenchJson.Write(Path.Combine(context.RunFolder, FileName), report);
        File.WriteAllText(Path.Combine(context.RunFolder, MarkdownName), Markdown(report));
        Console.WriteLine($"Overlay report: {Path.Combine(context.RunFolder, MarkdownName)}");
        return aborted is null && phases.Count > 0 && phases.All(p => p.AvgFps is not null) ? 0 : 1;
    }

    private static string Join(string? earlier, string next) => earlier is null ? next : $"{earlier}; {next}";

    /// <summary>Whether the dot can be on screen: its window is shown and the load's frames go through DWM's composition.</summary>
    internal static bool OnScreen(bool visible, string? presentMode) =>
        visible && presentMode is not null && !Exclusive.Contains(presentMode);

    internal static OverlayModeSummary Summarize(string mode, IReadOnlyList<OverlayPhase> phases, IReadOnlyList<string> fullscreenChanges)
    {
        OverlayPhase? Phase(string name) => phases.FirstOrDefault(p => p.Phase == name);
        var off = Phase("off");
        var on = Phase("on");
        var offAgain = Phase("off-again");
        var hidden = new[] { off?.AvgFps, offAgain?.AvgFps }.OfType<double>().ToList();
        double? fpsOff = hidden.Count == 0 ? null : Math.Round(hidden.Average(), 2);
        double? cost = fpsOff is > 0 && on?.AvgFps is { } fpsOn ? Math.Round((1 - (fpsOn / fpsOff.Value)) * 100, 2) : null;
        return new OverlayModeSummary(
            mode,
            off?.PresentMode,
            on?.PresentMode,
            offAgain?.PresentMode,
            fpsOff,
            on?.AvgFps,
            cost,
            IndependentFlipKept: off?.PresentMode is { } before && on?.PresentMode == before && before.Contains("Independent Flip", StringComparison.Ordinal),
            OverlayShows: on?.OverlayOnScreen ?? false,
            FullscreenD3D: phases.Any(p => p.NotificationState == QunsRunningD3DFullScreen),
            fullscreenChanges,
            // Every entry but the load's own notes ("load: ...") is a timed DXGI fullscreen change after ready.
            ExclusiveKept: phases.Count == 3 && phases.All(p => p.LoadInForeground && p.Error is null)
                && fullscreenChanges.All(c => c.StartsWith(LoadNotePrefix, StringComparison.Ordinal)));
    }

    internal static string Markdown(OverlayReport report)
    {
        var md = new StringBuilder();
        md.AppendLine("# Overlay over the GPU load (story 5.2, PRD Q-4)");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Created {report.Created:yyyy-MM-dd HH:mm:ss zzz}. Overlay host: {report.Host}.{(report.OverlayDot is { } dot ? $" Dot mode: {dot}." : "")} PresentMon {report.Seconds} s per phase after {report.Settle} s settling.");
        md.AppendLine(report.RemoteSession
            ? $"**Remote Desktop session: every figure here is {RemoteLabel}.**"
            : "Console session (not Remote Desktop).");
        for (var i = 0; i < report.Loads.Count; i++)
        {
            var load = report.Loads[i];
            var planes = report.OverlayPlanes is { } p && i < p.Count ? p[i] switch { true => "yes", false => "no", null => "unknown" } : "not recorded";
            md.AppendLine(CultureInfo.InvariantCulture, $"- Load `{load.Mode}`: {load.Width}x{load.Height} on {load.Adapter}, calibrated {Num(load.CalibratedFps)} fps (target {Num(load.TargetFps)}), tearing {load.Tearing}, DXGI fullscreen at ready {load.Fullscreen}, overlay planes on its monitor (SupportsOverlays) {planes}.");
        }

        if (report.Overlay is { } overlay)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"- Overlay window: extended style 0x{overlay.ExtendedStyle:X}, excluded from capture {overlay.Excluded}.");
        }

        if (report.Aborted is { } aborted)
        {
            md.AppendLine();
            md.AppendLine($"**Aborted:** {aborted}");
        }

        md.AppendLine();
        md.AppendLine("## Summary");
        md.AppendLine();
        md.AppendLine("| Mode | PresentMode, dot hidden | PresentMode, dot shown | PresentMode, hidden again | FPS hidden | FPS shown | FPS cost | Independent flip kept | Dot on screen | QUNS D3D fullscreen | Fullscreen and focus kept |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in report.Summaries)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {s.Mode} | {s.PresentModeOff} | {s.PresentModeOn} | {s.PresentModeOffAgain} | {Num(s.FpsOff)} | {Num(s.FpsOn)} | {Pct(s.FpsCostPercent)} | {YesNo(s.IndependentFlipKept)} | {YesNo(s.OverlayShows)} | {YesNo(s.FullscreenD3D)} | {YesNo(s.ExclusiveKept)} |");
        }

        md.AppendLine();
        md.AppendLine("## Phases");
        md.AppendLine();
        md.AppendLine("| Mode | Phase | Frames | Avg FPS | 1 % low | PresentMode (share) | SHQueryUserNotificationState | Load in front | Dot window visible | Error |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var p in report.Phases)
        {
            var shares = string.Join("; ", p.PresentModePercent.OrderByDescending(m => m.Value).Select(m => $"{m.Key} {m.Value:0.#} %"));
            md.AppendLine(CultureInfo.InvariantCulture, $"| {p.Mode} | {p.Phase} | {p.Frames} | {Num(p.AvgFps)} | {Num(p.OnePercentLowFps)} | {shares} | {p.NotificationStateName} ({p.NotificationState}) | {YesNo(p.LoadInForeground)} | {YesNo(p.OverlayWindowVisible)} | {p.Error} |");
        }

        if (report.Summaries.Any(s => s.FullscreenChanges.Count > 0))
        {
            md.AppendLine();
            md.AppendLine("## DXGI fullscreen changes and the load's notes");
            md.AppendLine();
            foreach (var s in report.Summaries.Where(s => s.FullscreenChanges.Count > 0))
            {
                md.AppendLine($"- {s.Mode}: {string.Join(", ", s.FullscreenChanges)}");
            }
        }

        md.AppendLine();
        md.AppendLine("Dot on screen: its window is visible and the load's frames go through DWM (any PresentMode but Hardware: Legacy Flip or Legacy Copy). FPS cost is against the mean of the two hidden phases. Fullscreen and focus kept: the load was in front before and after every phase, never exited and reported no DXGI fullscreen change.");
        return md.ToString();
    }

    /// <summary>Records the process's presents for <paramref name="seconds"/>; returns why it failed, or null.</summary>
    private static string? RecordPresents(string presentMon, string processName, string csv, int seconds, string folder, string tag)
    {
        var session = $"Encore-Overlay-{Environment.ProcessId}";
        var start = new ProcessStartInfo(presentMon)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "--process_name", processName, "--output_file", csv, "--timed", seconds.ToString(CultureInfo.InvariantCulture),
            "--terminate_after_timed", "--no_console_stats", "--stop_existing_session", "--session_name", session,
        })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new BenchFailure($"Could not start {presentMon}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(seconds + 60)))
        {
            process.Kill();
            process.WaitForExit(5000);
            using var stop = Process.Start(new ProcessStartInfo("logman", $"stop {session} -ets") { UseShellExecute = false, CreateNoWindow = true });
            stop?.WaitForExit(10000);
            return "PresentMon did not stop on its own and was killed";
        }

        File.WriteAllText(Path.Combine(folder, $"{tag}-presentmon.log"), output.Result + error.Result);
        return process.ExitCode == 0 ? null : $"PresentMon exited with {process.ExitCode}";
    }

    /// <summary>Brings a window to the front by sharing the foreground thread's input state, as the load itself does.</summary>
    private static void BringToFront(nint hwnd)
    {
        var foreground = Encore.Platform.Desktop.OverlayWindowStyle.ForegroundWindow();
        if (foreground == hwnd)
        {
            return;
        }

        var foregroundThread = GetWindowThreadProcessId(foreground, 0);
        var thread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(thread, foregroundThread, false);
            }
        }
    }

    private static int NotificationState() => SHQueryUserNotificationState(out var state) == 0 ? state : 0;

    internal static string NotificationStateName(int state) => state switch
    {
        1 => "QUNS_NOT_PRESENT",
        2 => "QUNS_BUSY",
        3 => "QUNS_RUNNING_D3D_FULL_SCREEN",
        4 => "QUNS_PRESENTATION_MODE",
        5 => "QUNS_ACCEPTS_NOTIFICATIONS",
        6 => "QUNS_QUIET_TIME",
        7 => "QUNS_APP",
        _ => "unknown",
    };

    /// <summary>Whether this is a Remote Desktop session (<c>SM_REMOTESESSION</c>).</summary>
    public static bool IsRemoteSession() => GetSystemMetrics(SmRemoteSession) != 0;

    private static string Num(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "n/a";

    private static string Pct(double? value) => value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) + " %" : "n/a";

    private static string YesNo(bool value) => value ? "yes" : "no";

    private const int SmRemoteSession = 0x1000;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, nint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);
}

/// <summary>What one PresentMon CSV says: frames, mean and 1 % low FPS, and each PresentMode's share.</summary>
internal sealed record PresentMonSummary(
    int Frames, double? AvgFps, double? OnePercentLowFps, string? DominantMode, IReadOnlyDictionary<string, double> ModePercent, string? Error);

/// <summary>Reads a PresentMon capture file into the figures the overlay report shows.</summary>
internal static class PresentMonCsv
{
    /// <summary>Reads a PresentMon CSV: the frame time column of either version, and every present's mode.</summary>
    public static PresentMonSummary Analyze(string path)
    {
        if (!File.Exists(path))
        {
            return new PresentMonSummary(0, null, null, null, new Dictionary<string, double>(), "PresentMon wrote no CSV");
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length < 2)
        {
            return new PresentMonSummary(0, null, null, null, new Dictionary<string, double>(), "PresentMon recorded no presents");
        }

        var header = lines[0].TrimStart('﻿').Split(',');
        var timeColumn = Array.IndexOf(header, "MsBetweenPresents") is var ms and >= 0 ? ms : Array.IndexOf(header, "FrameTime");
        var modeColumn = Array.IndexOf(header, "PresentMode");
        if (timeColumn < 0 || modeColumn < 0)
        {
            return new PresentMonSummary(0, null, null, null, new Dictionary<string, double>(), $"no frame-time or PresentMode column in {lines[0]}");
        }

        var times = new List<double>();
        var modes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (cells.Length <= Math.Max(timeColumn, modeColumn))
            {
                continue;
            }

            modes[cells[modeColumn]] = modes.GetValueOrDefault(cells[modeColumn]) + 1;
            if (double.TryParse(cells[timeColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var time) && time > 0)
            {
                times.Add(time);
            }
        }

        var total = modes.Values.Sum();
        var percent = modes.ToDictionary(m => m.Key, m => Math.Round(100.0 * m.Value / total, 2), StringComparer.Ordinal);
        var dominant = modes.Count == 0 ? null : modes.MaxBy(m => m.Value).Key;
        if (times.Count == 0)
        {
            return new PresentMonSummary(0, null, null, dominant, percent, "no usable frame times");
        }

        times.Sort();
        var p99 = times[Math.Clamp((int)Math.Ceiling(0.99 * times.Count) - 1, 0, times.Count - 1)];
        return new PresentMonSummary(times.Count, Math.Round(1000 / times.Average(), 2), Math.Round(1000 / p99, 2), dominant, percent, null);
    }
}
