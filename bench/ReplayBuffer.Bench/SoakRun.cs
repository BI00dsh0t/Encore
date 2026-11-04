using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Bench;

/// <summary>One Save press of the soak and its Clip.</summary>
/// <param name="Outcome"><c>saved</c>, <c>failed</c> (<c>clip.failed</c>), <c>no-event</c> (nothing within 30 s) or <c>not-pressed</c> (SendInput refused).</param>
internal sealed record SoakClipRow(int Press, DateTimeOffset At, double ElapsedSeconds, string Outcome, string? Path, SoakClipResult? Check, bool Pass, string? Failure)
{
    public const string CsvHeader = "press,time,elapsedSeconds,outcome,pass,videoSeconds,audioSeconds,tracks,frames,expectedFrames,path,failure";

    public string CsvRow() => string.Join(
        ',',
        Press.ToString(CultureInfo.InvariantCulture),
        At.ToString("O", CultureInfo.InvariantCulture),
        ElapsedSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        Outcome,
        Pass ? "pass" : "fail",
        Number(Check?.VideoSeconds),
        Csv(string.Join(';', Check?.AudioSeconds.Select(Number) ?? [])),
        Csv(string.Join(';', Check?.Tracks ?? [])),
        Check?.Frames?.ToString(CultureInfo.InvariantCulture) ?? "",
        Number(Check?.ExpectedFrames),
        Csv(Path ?? ""),
        Csv(Failure ?? ""));

    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";

    private static string Csv(string text) => text.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
}

/// <summary><c>soak.json</c>.</summary>
internal sealed record SoakReport(
    DateTimeOffset Created,
    string RunFolder,
    string Instance,
    string Mode,
    double Minutes,
    double WarmupMinutes,
    int BufferSeconds,
    int PressSeconds,
    int SampleSeconds,
    string Key,
    string App,
    string Engine,
    GpuLoadInfo? Load,
    IReadOnlyList<string> Tracks,
    double Fps,
    int Samples,
    int Presses,
    IReadOnlyList<SoakClipRow> Clips,
    IReadOnlyList<SoakBound> Bounds,
    string? Stopped,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Leftovers,
    string Verdict);

/// <summary>
/// <c>bench soak</c> (story 8.1): the real engine (<c>serve --instance … --replay-buffer</c>) and App
/// (<c>--tray --instance …</c>) as separate processes on a throwaway test instance, with the synthetic GPU
/// load as the Target Game. The Save Hotkey is injected every <c>--press-seconds</c>; both processes are
/// sampled every <c>--sample-seconds</c>; every Clip is checked with ffprobe and ffmpeg. Writes
/// <c>soak-samples.csv</c>, <c>soak-clips.csv</c>, <c>soak.json</c> and <c>soak.md</c>. Holds the
/// <c>.bench-running</c> lock for the whole run. Exit 0 only when every Clip passes, at least one was made,
/// and every bound is met.
/// </summary>
internal static class SoakRun
{
    public const string SamplesFile = "soak-samples.csv";
    public const string ClipsFile = "soak-clips.csv";
    public const string JsonFile = "soak.json";
    public const string MarkdownFile = "soak.md";
    public const string LockName = ".bench-running";

    private const string EngineExe = "ReplayBuffer.Engine.exe";
    private static readonly TimeSpan ClipTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    public static int Run(BenchContext context)
    {
        var options = context.Options;
        var minutes = options.Double("minutes", 20, 0.1, 7 * 24 * 60);
        var storage = options.Text("mode", "memory") switch
        {
            "memory" => ReplayStorage.Memory,
            "disk" => ReplayStorage.Disk,
            var other => throw new BenchUsageException($"--mode '{other}' is not memory or disk."),
        };
        var pressSeconds = options.Int("press-seconds", 60, 1, 24 * 3600);
        var bufferSeconds = options.Int("buffer-seconds", 30, 1, 3600);
        var sampleSeconds = options.Int("sample-seconds", 10, 1, 3600);
        var warmupMinutes = options.Double("warmup-minutes", Math.Min(30, minutes / 4), 0, minutes);
        var combo = KeyCombo.Parse(options.Text("key", "ctrl+alt+shift+f16"));
        var targetFps = options.Double("target-fps", GpuLoadProcess.DefaultTargetFps, 1, 1000);
        var loadSize = options.Text("load-size");
        if (loadSize is not null)
        {
            BenchOptions.ParseSize(loadSize, "load-size");
        }

        using var held = BenchLock.Acquire(options.Text("lock") is { } given ? Path.GetFullPath(given) : DefaultLock(context.RepoRoot));

        var appExe = AppOverlayHost.Resolve(context);
        var engineExe = Path.Combine(Path.GetDirectoryName(appExe)!, EngineExe);
        if (!File.Exists(engineExe))
        {
            throw new BenchFailure($"No {EngineExe} beside the App at {appExe}: the App connects only to the engine in its own folder.");
        }

        var loadExe = GpuLoadProcess.Resolve(context);
        var tools = context.ResolveFfmpeg();

        var run = context.RunFolder;
        var clips = Directory.CreateDirectory(Path.Combine(run, "clips")).FullName;
        var ring = Directory.CreateDirectory(Path.Combine(run, "ring")).FullName;
        var instance = Instance.ForTest(Instance.TestPrefix + "soak-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
        var modeText = storage == ReplayStorage.Memory ? "memory" : "disk";
        Console.WriteLine($"Soak: {minutes:0.##} min, {modeText}, a {bufferSeconds} s Buffer Window, {combo.Text} every {pressSeconds} s, samples every {sampleSeconds} s, a {warmupMinutes:0.##}-minute warm-up; instance {instance.Name}.");

        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        var errors = new List<string>();
        var rows = new List<SoakClipRow>();
        var samples = new Dictionary<string, List<ProcessCounters>> { ["engine"] = [], ["app"] = [] };
        var titles = (IReadOnlyList<string>)[];
        var fps = 0.0;
        string? stopped = null;
        var schedule = new SoakSchedule(TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(sampleSeconds), TimeSpan.FromSeconds(pressSeconds), TimeProvider.System);
        Process? engine = null;
        Process? app = null;
        EngineClient? client = null;
        GpuLoadProcess? load = null;
        GpuLoadInfo? loadInfo = null;
        var events = Channel.CreateUnbounded<IpcEvent>();
        List<string> leftovers;
        try
        {
            engine = HotkeyBenchmark.StartServe(engineExe, instance.Name, Path.Combine(run, "soak"), cancel.Token, "--replay-buffer");
            client = EngineClient.ConnectAsync(
                PipeName.ForCurrentUser(instance.Name),
                new EngineClientOptions
                {
                    OnEvent = e =>
                    {
                        if (e.Type == CaptureMessages.Saved.Name || e.Type == CaptureMessages.Failed.Name)
                        {
                            events.Writer.TryWrite(e);
                        }
                    },
                },
                cancel.Token).WaitAsync(StartTimeout, cancel.Token).GetAwaiter().GetResult();

            Update(client,
                SettingsOp.SetGlobal(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(clips)),
                SettingsOp.SetGlobal(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(bufferSeconds)),
                SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(0)),
                SettingsOp.SetGlobal(Keys.ReplayStorage.Name, Keys.ReplayStorage.ToJson(storage)),
                SettingsOp.SetGlobal(Keys.RingPath.Name, Keys.RingPath.ToJson(ring)),
                SettingsOp.SetGlobal(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(new HotkeySettings(new HotkeyBinding(combo.Vk, combo.Modifiers), null, null, null, null, null))));
            var save = Until(client, c => c.State.Hotkeys.Bindings.FirstOrDefault(b => b.Action == HotkeyAction.Save) is { Code: not null } or { Registered: true },
                TimeSpan.FromSeconds(10), "the Save Hotkey's status", cancel.Token);
            var status = client.State.Hotkeys.Bindings.First(b => b.Action == HotkeyAction.Save);
            if (!status.Registered)
            {
                throw new BenchFailure($"{status.Code}: the engine did not register {combo.Text}; pick another --key");
            }

            app = StartApp(appExe, instance);
            var appPid = app.Id;
            Until(client, c => c.State.AppPid == appPid, StartTimeout, "the App to attach", cancel.Token, () => app.HasExited ? "the App exited" : null);
            Console.WriteLine($"Engine {engine.Id} and App {app.Id} up.");

            load = GpuLoadProcess.Start(loadExe, targetFps, loadSize, null, StartTimeout);
            loadInfo = load.Info;
            Wait(client.SendAsync(GameMessages.Add, new GameAdd(loadExe), cancel.Token));
            var profile = AppOverlayHost.Canonicalize(loadExe);
            Until(client, c => c.State.Capture.Replay == ReplayState.Buffering && c.State.TargetGame?.ProfileId == profile,
                StartTimeout, "the Replay Buffer buffering on the GPU load", cancel.Token, () => load.HasExited ? "the GPU load exited" : null);

            var global = client.State.Settings.Global;
            titles = [.. Read(global, Keys.TrackLayout).Tracks.Select(t => t.Name)];
            fps = Read(global, Keys.Video).Fps;
            Console.WriteLine($"Buffering on the GPU load; Tracks {string.Join(", ", titles)}, {fps:0.##} fps. Filling one Buffer Window ({bufferSeconds} s).");
            cancel.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(bufferSeconds + 2));

            var start = DateTimeOffset.UtcNow;
            using var samplesCsv = new StreamWriter(Path.Combine(run, SamplesFile)) { AutoFlush = true };
            samplesCsv.WriteLine(ProcessCounters.CsvHeader);
            var checks = Task.CompletedTask;
            var pressCount = 0;
            string? injectFault = null;
            var (engineProcess, appProcess, loadProcess) = (engine, app, load);
            var checkTitles = titles;
            var checkFps = fps;

            void Sample()
            {
                foreach (var (name, process) in new[] { ("engine", engineProcess), ("app", appProcess) })
                {
                    try
                    {
                        var counters = SoakSampler.Sample(process, name);
                        samples[name].Add(counters);
                        samplesCsv.WriteLine(counters.CsvRow(start));
                    }
                    catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                    {
                        // It has exited; the stop check ends the run.
                    }
                }
            }

            void Press()
            {
                var press = ++pressCount;
                var at = DateTimeOffset.UtcNow;
                var elapsed = (at - start).TotalSeconds;
                while (events.Reader.TryRead(out _))
                {
                    // A late event from an earlier press: that press already timed out.
                }

                if (KeyInjector.PressOnce(combo) is { } fault)
                {
                    injectFault = fault;
                    Add(new SoakClipRow(press, at, elapsed, "not-pressed", null, null, false, fault));
                    return;
                }

                IpcEvent? result = null;
                try
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
                    wait.CancelAfter(ClipTimeout);
                    result = events.Reader.ReadAsync(wait.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // No event: recorded below.
                }

                if (result is null)
                {
                    Add(new SoakClipRow(press, at, elapsed, "no-event", null, null, false, $"no clip.saved or clip.failed within {ClipTimeout.TotalSeconds:0} s"));
                    return;
                }

                if (result.Type == CaptureMessages.Failed.Name)
                {
                    var reason = CaptureMessages.Failed.Read(result.Payload).Reason;
                    Add(new SoakClipRow(press, at, elapsed, "failed", null, null, false, "clip.failed: " + reason));
                    return;
                }

                var path = CaptureMessages.Saved.Read(result.Payload).Path;
                Console.WriteLine($"  press {press} at {elapsed:0} s: {path}");
                checks = checks.ContinueWith(_ => CheckClip(tools, press, at, elapsed, path, checkTitles, bufferSeconds, checkFps, Add), TaskScheduler.Default);
            }

            void Add(SoakClipRow row)
            {
                lock (rows)
                {
                    rows.Add(row);
                }

                if (!row.Pass)
                {
                    Console.WriteLine($"  press {row.Press} failed: {row.Failure}");
                }
            }

            string? Stop() =>
                engineProcess.HasExited ? $"the engine exited (code {ExitCode(engineProcess)})"
                : appProcess.HasExited ? $"the App exited (code {ExitCode(appProcess)})"
                : loadProcess.HasExited ? "the GPU load exited: " + loadProcess.ExitDescription()
                : injectFault is not null ? "the Save Hotkey could not be injected"
                : null;

            stopped = schedule.Run(Sample, Press, Stop, cancel.Token);
            checks.GetAwaiter().GetResult();
            Console.WriteLine($"Soak loop done: {schedule.Samples} samples, {schedule.Presses} presses{(stopped is not null ? "; stopped early: " + stopped : "")}.");
        }
        catch (Exception e) when (e is BenchFailure or ErrorCodeException or TimeoutException or OperationCanceledException or IOException or Win32Exception)
        {
            errors.Add(e is OperationCanceledException ? "stopped before the soak loop (Ctrl+C)" : e.Message);
            Console.Error.WriteLine("error: " + errors[^1]);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;

            // On every path, even an exception the catch above lets through.
            leftovers = Cleanup(engine, app, client, load, instance, ring);
        }

        var warmup = TimeSpan.FromMinutes(warmupMinutes);
        var bounds = schedule.Samples > 0
            ? SoakBounds.Evaluate(samples.ToDictionary(p => p.Key, p => (IReadOnlyList<ProcessCounters>)p.Value), warmup)
            : [];
        var ordered = rows.OrderBy(r => r.Press).ToList();
        var pass = stopped is null && errors.Count == 0 && leftovers.Count == 0
            && ordered.Count > 0 && ordered.Count == schedule.Presses && ordered.All(r => r.Pass) && bounds.Count > 0 && bounds.All(b => b.Pass);
        var report = new SoakReport(
            DateTimeOffset.Now, run, instance.Name, modeText, minutes, warmupMinutes, bufferSeconds, pressSeconds, sampleSeconds, combo.Text,
            appExe, engineExe, loadInfo, titles, fps, schedule.Samples, schedule.Presses, ordered, bounds, stopped, errors, leftovers,
            pass ? DriftMath.Pass : DriftMath.Miss);

        File.WriteAllLines(Path.Combine(run, ClipsFile), [SoakClipRow.CsvHeader, .. ordered.Select(r => r.CsvRow())]);
        if (!File.Exists(Path.Combine(run, SamplesFile)))
        {
            File.WriteAllLines(Path.Combine(run, SamplesFile), [ProcessCounters.CsvHeader]);
        }

        BenchJson.Write(Path.Combine(run, JsonFile), report);
        File.WriteAllText(Path.Combine(run, MarkdownFile), Markdown(report));
        Console.WriteLine($"Soak {report.Verdict}: {ordered.Count(r => r.Pass)} of {ordered.Count} Clip(s) pass, {bounds.Count(b => b.Pass)} of {bounds.Count} bound(s) met.");
        Console.WriteLine($"Report: {Path.Combine(run, MarkdownFile)}");
        return pass ? 0 : 1;
    }

    /// <summary>
    /// <c>.bench-running</c> in the main checkout: the folder above <c>.claude\worktrees\&lt;name&gt;</c> when
    /// <paramref name="repoRoot"/> is a worktree, else the repo root.
    /// </summary>
    internal static string DefaultLock(string repoRoot)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar));
        var main = dir.Parent is { Name: "worktrees" } worktrees && worktrees.Parent is { Name: ".claude" } claude && claude.Parent is { } root
            ? root.FullName
            : dir.FullName;
        return Path.Combine(main, LockName);
    }

    private static void CheckClip(
        FfmpegTools tools, int press, DateTimeOffset at, double elapsed, string path, IReadOnlyList<string> titles, int bufferSeconds, double fps, Action<SoakClipRow> add)
    {
        SoakClipResult check;
        try
        {
            check = SoakClipCheck.Check(tools, path, titles, bufferSeconds, fps);
        }
        catch (Exception e)
        {
            // Any failure to check is a failed row: a press never goes without one.
            add(new SoakClipRow(press, at, elapsed, "saved", path, null, false, "check: " + e.Message));
            return;
        }

        add(new SoakClipRow(press, at, elapsed, "saved", path, check, check.Pass, check.Pass ? null : string.Join("; ", check.Failures)));
        if (check.Pass)
        {
            // A passing Clip has told what it can; deleting it keeps a long run's disk use flat.
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not delete {path}: {e.Message}");
            }
        }
    }

    private static Process StartApp(string exe, Instance instance)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--tray", "--instance", instance.Name })
        {
            start.ArgumentList.Add(arg);
        }

        return Process.Start(start) ?? throw new BenchFailure($"Could not start {exe}.");
    }

    /// <summary>Shuts the engine down (the App quits with it), stops whatever is left, deletes the instance's folders; returns what survived.</summary>
    private static List<string> Cleanup(Process? engine, Process? app, EngineClient? client, GpuLoadProcess? load, Instance instance, string ring)
    {
        var leftovers = new List<string>();
        var appPid = app?.Id;
        try
        {
            if (app is not null)
            {
                AppOverlayHost.Cleanup(app, instance, client, engine?.Id ?? 0);
            }
            else if (client is not null)
            {
                try
                {
                    Wait(client.SendAsync(EngineMessages.Shutdown, Empty.Value));
                }
                catch (Exception e) when (e is ErrorCodeException or TimeoutException or IOException or ObjectDisposedException)
                {
                    // It is killed below.
                }

                client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception e) when (e is ErrorCodeException or TimeoutException or IOException or ObjectDisposedException or AggregateException)
        {
            leftovers.Add("cleanup: " + e.Message);
        }

        if (engine is not null)
        {
            try
            {
                if (!engine.WaitForExit(TimeSpan.FromSeconds(10)))
                {
                    engine.Kill(entireProcessTree: true);
                    engine.WaitForExit(5000);
                }

                if (!engine.HasExited)
                {
                    leftovers.Add($"engine process {engine.Id}");
                }
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                // It exited in between.
            }

            engine.Dispose();
        }

        // AppOverlayHost.Cleanup disposed the App's Process: look it up again by its id.
        if (appPid is { } pid)
        {
            try
            {
                using var survivor = Process.GetProcessById(pid);
                if (survivor.ProcessName == Path.GetFileNameWithoutExtension(AppOverlayHost.ExeName) && !survivor.HasExited)
                {
                    leftovers.Add($"App process {pid}");
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Gone.
            }
        }

        if (load is not null)
        {
            var id = load.Id;
            load.Dispose();
            if (GpuLoadProcess.Running().Contains(id))
            {
                leftovers.Add($"GPU load process {id}");
            }
        }

        foreach (var folder in new[] { instance.RoamingDataFolder, instance.LocalDataFolder, ring })
        {
            AppOverlayHost.DeleteQuietly(folder);
            if (Directory.Exists(folder))
            {
                leftovers.Add("folder " + folder);
            }
        }

        foreach (var leftover in leftovers)
        {
            Console.Error.WriteLine("left behind: " + leftover);
        }

        return leftovers;
    }

    private static string ExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    private static T Read<T>(System.Collections.Immutable.ImmutableSortedDictionary<string, JsonElement> global, SettingKey<T> key) =>
        global.TryGetValue(key.Name, out var value) && key.TryRead(value, out var read) ? read : key.Default;

    private static void Update(EngineClient client, params SettingsOp[] ops)
    {
        var result = Wait(client.SendAsync(SettingsMessages.Update, new SettingsUpdate(client.State.Settings.Revision, ops)));
        Until(client, c => c.State.Settings.Revision >= result.Revision, TimeSpan.FromSeconds(10), "settings.changed", CancellationToken.None);
    }

    private static bool Until(EngineClient client, Func<EngineClient, bool> condition, TimeSpan timeout, string what, CancellationToken cancellation, Func<string?>? abort = null)
    {
        var clock = Stopwatch.StartNew();
        while (!condition(client))
        {
            cancellation.ThrowIfCancellationRequested();
            if (abort?.Invoke() is { } why)
            {
                throw new BenchFailure($"Waiting for {what}: {why}.");
            }

            if (clock.Elapsed > timeout)
            {
                throw new BenchFailure($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}.");
            }

            Thread.Sleep(100);
        }

        return true;
    }

    private static T Wait<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

    internal static string Markdown(SoakReport report)
    {
        var md = new StringBuilder();
        void Line(FormattableString text) => md.AppendLine(text.ToString(CultureInfo.InvariantCulture));

        Line($"# ReplayBuffer soak, {report.Created:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        Line($"**Verdict: {report.Verdict}.** {report.Clips.Count(c => c.Pass)} of {report.Clips.Count} Clip(s) pass; {report.Bounds.Count(b => b.Pass)} of {report.Bounds.Count} bound(s) met.");
        md.AppendLine();
        Line($"- Run: {report.Minutes:0.##} min in {report.Mode} mode, a {report.WarmupMinutes:0.##}-minute warm-up, {report.Samples} samples (every {report.SampleSeconds} s), {report.Presses} presses of {report.Key} (every {report.PressSeconds} s)");
        Line($"- Buffer Window {report.BufferSeconds} s; Tracks {string.Join(", ", report.Tracks)}; {report.Fps:0.##} fps");
        Line($"- Instance `{report.Instance}`; App `{report.App}`; engine `{report.Engine}`");
        if (report.Load is { } load)
        {
            Line($"- GPU load: {load.Width}x{load.Height} on {load.Adapter}, {load.CalibratedFps:0.#} fps (target {load.TargetFps:0.#})");
        }

        Line($"- Run folder: `{report.RunFolder}`");
        if (report.Stopped is { } stopped)
        {
            Line($"- **Stopped early:** {stopped}");
        }

        foreach (var error in report.Errors)
        {
            Line($"- **Error:** {error}");
        }

        foreach (var leftover in report.Leftovers)
        {
            Line($"- **Left behind:** {leftover}");
        }

        md.AppendLine();
        md.AppendLine("## Bounds");
        md.AppendLine();
        md.AppendLine("| Process | Counter | Value | Limit | Result |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var bound in report.Bounds)
        {
            Line($"| {bound.Process} | {bound.Counter} | {bound.Detail} | {bound.Limit:0.##} | {(!bound.Judged ? "info" : bound.Pass ? "pass" : "**fail**")} |");
        }

        md.AppendLine();
        md.AppendLine("## Clips");
        md.AppendLine();
        md.AppendLine("| Press | At (s) | Outcome | Video (s) | Audio (s) | Tracks | Frames / expected | Result |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var clip in report.Clips)
        {
            var check = clip.Check;
            var audio = check is null ? "" : string.Join(", ", check.AudioSeconds.Select(a => a?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?"));
            var frames = check is null ? "" : $"{check.Frames?.ToString(CultureInfo.InvariantCulture) ?? "?"} / {check.ExpectedFrames?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?"}";
            Line($"| {clip.Press} | {clip.ElapsedSeconds:0} | {clip.Outcome} | {check?.VideoSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? ""} | {audio} | {(check is null ? "" : string.Join(", ", check.Tracks))} | {frames} | {(clip.Pass ? "pass" : "**fail**: " + clip.Failure)} |");
        }

        md.AppendLine();
        md.AppendLine("Passing Clips are deleted after their check; a failing one stays in the run folder's `clips` folder.");
        return md.ToString();
    }
}

/// <summary>
/// The <c>.bench-running</c> lock: created with <see cref="FileMode.CreateNew"/>, so only one soak or heavy run
/// holds it, and deleted on dispose.
/// </summary>
internal sealed class BenchLock : IDisposable
{
    private readonly string _path;

    private BenchLock(string path) => _path = path;

    /// <exception cref="BenchFailure">The lock file already exists.</exception>
    public static BenchLock Acquire(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(file);
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"bench soak, pid {Environment.ProcessId}, {DateTimeOffset.Now:O}"));
        }
        catch (IOException) when (File.Exists(path))
        {
            throw new BenchFailure($"{path} exists: another Bench run or heavy test session holds the lock. Wait for it, or delete the file if it is stale.");
        }

        Console.WriteLine($"Holding {path}.");
        return new BenchLock(path);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not delete the lock {_path}: {e.Message}");
        }
    }
}
