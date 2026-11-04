using System.Globalization;
using System.Security.Principal;

namespace ReplayBuffer.Bench;

/// <summary>A command line Bench can't run: printed with the usage, exit code 2.</summary>
internal sealed class BenchUsageException(string message) : Exception(message);

/// <summary>A run that can't go on (nothing to measure, a tool missing): printed, exit code 1.</summary>
internal sealed class BenchFailure(string message) : Exception(message);

/// <summary>
/// Bench's command line: a subcommand, then <c>--name value</c> options and a few bare flags.
/// Every value Bench uses has an option with a default, so a later epic can rerun the same benchmark.
/// </summary>
internal sealed class BenchOptions
{
    public const string Usage =
        """
        Usage:
          ReplayBuffer.Bench fps      [--game <process>] [--seconds 300] [--passes 3] [--settle 20]
                                      [--target-fps 80] [--load-size WxH] [--load-timeout 60] [--load-warmup 10]
                                      [--load-exe <ReplayBuffer.Bench.GpuLoad.exe>]
                                      [--presentmon <exe>] [--pwsh <exe>] [--no-gpu-sampler] <capture options>
                                      [--overlay [--overlay-dot auto|always|onChange] [--app-exe <ReplayBuffer.App.exe>]]
          ReplayBuffer.Bench drift    [--minutes 60] [--tone-dbfs -30] [--bucket-seconds 60] [--burst-track 0]
                                      [--reanalyze] <capture options>
          ReplayBuffer.Bench playback [--seconds 30] [--players "Discord,Chrome,VLC,Windows Media Player,DaVinci Resolve"]
                                      [--tone-dbfs -30] [--auto [--chrome <exe>] [--play-seconds 5] [--player-timeout 60]]
                                      <capture options>
          ReplayBuffer.Bench hotkey   [--presses 20] [--key ctrl+alt+f9] [--timeout 60] [--inject]
          ReplayBuffer.Bench tracks   --device-a <Output Device> --device-b <Output Device> [--input-device <name or id>]
                                      [--minutes 5] [--tone-dbfs 0] [--size 1920x1080] [--fps 60] [--bitrate 20000]
                                      [--vlc <vlc.exe>] [--reanalyze]
          ReplayBuffer.Bench overlay  [--modes borderless,fullscreen,exclusive] [--seconds 15] [--settle 5]
                                      [--target-fps 80] [--load-size WxH] [--load-timeout 60] [--load-warmup 10]
                                      [--load-exe <exe>] [--overlay-exe <ReplayBuffer.Bench.Overlay.exe>] [--presentmon <exe>]
                                      [--host stand-in|app] [--app-exe <ReplayBuffer.App.exe>] [--dot-only]
                                      [--overlay-dot auto|always|onChange]
          ReplayBuffer.Bench soak     [--minutes 20] [--mode memory|disk] [--press-seconds 60] [--buffer-seconds 30]
                                      [--sample-seconds 10] [--warmup-minutes min(30, minutes/4)] [--key ctrl+alt+shift+f16]
                                      [--target-fps 80] [--load-size WxH] [--load-exe <exe>] [--app-exe <ReplayBuffer.App.exe>]
                                      [--lock <.bench-running>] [--kill-engine <min>] [--kill-app <min>] [--hang-engine <min>]
          ReplayBuffer.Bench report

        Capture options: [--size 1920x1080] [--fps 60] [--bitrate 20000] [--output-device <name or id>]
                         [--input-device <name or id>]
        Every command:   [--out <run folder>] [--repo <repo root>] [--engine <ReplayBuffer.Engine.exe>]
                         [--ffmpeg <folder with ffmpeg.exe and ffprobe.exe>]

        Each measurement writes its JSON into the run folder (default measurements/bench-<time>/, or --out;
        report defaults to the newest bench-* folder) and regenerates report.md and report.json there.

        Without --game, fps measures Bench's own synthetic GPU load (ReplayBuffer.Bench.GpuLoad), calibrated to
        --target-fps over the primary monitor (or --load-size). hotkey --inject presses the key with SendInput
        about once a second; playback --auto scripts Chrome (headless) and Windows Media Player and records
        every other player as not-checked. All three need nobody at the PC.

        tracks plays a tone with a 3 kHz burst each second into each of two Output Devices (B half a second after
        A, both at --tone-dbfs), captures a four-Track layout (Everything = A + B + Mic, Game = A, Chat = B,
        Mic = the Input Device) in a throwaway test instance, and checks the Track names and order, Track 1 as
        the only default, their lengths, sync, levels and full scale, and VLC's listing (when VLC is installed).
        Exit 0 when every check passes. --reanalyze checks tracks.mp4 again without devices.

        overlay (story 5.2) runs the GPU load in each mode (borderless; fullscreen: DXGI fullscreen on a flip
        swap chain; exclusive: a blt swap chain with fullscreen optimizations off) with story 5.1's Overlay dot
        hidden, shown and hidden again (a minute per mode by default), and writes each phase's PresentMode, FPS and SHQueryUserNotificationState
        to overlay.json and overlay.md. --host app (story 5.8) runs the real App on a throwaway test instance instead
        of the stand-in, and shows its solid dot plus a saved and a failed message through dev.simulate (the dot
        alone with --dot-only). --overlay-dot sets the App's overlayDot (default auto), and the report gives
        whether each load's monitor has overlay planes.

        fps --overlay (story 5.12) runs the real App on a throwaway test instance beside the script: its dot and
        a message every 10 s are on during the capture runs only, off during the baseline runs.

        soak (story 8.1) runs the real engine (serve --replay-buffer) and App (--tray) from the App's Release folder
        on a throwaway test instance, with the GPU load as the Target Game; presses the Save Hotkey every
        --press-seconds, samples both processes' private bytes, working set, handles and GDI and USER objects every
        --sample-seconds, and checks every Clip with ffprobe and ffmpeg. Writes soak-samples.csv, soak-clips.csv,
        soak.json and soak.md; exit 0 only when every Clip passes and every bound is met. Holds the lock file
        (default .bench-running in the main checkout) for the whole run, and refuses to start while it exists.
        --kill-engine, --kill-app and --hang-engine (story 8.2) each kill the engine, kill the App or hang the
        engine (dev.simulate engineHang) once, that many minutes into the loop, and check the recovery: the
        engine is back buffering with engine-restarted within 10 s (20 s for a hang), or a Save press with the
        App gone saves a passing Clip and the App is back with app-restarted within 10 s. Every other press
        waits for a whole Buffer Window since the last save or engine restart.
        """;

    private static readonly string[] Common = ["out", "repo", "engine", "ffmpeg"];
    private static readonly string[] Capture = ["size", "fps", "bitrate", "output-device", "input-device"];

    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["fps"] = [.. Common, .. Capture, "game", "seconds", "passes", "settle", "presentmon", "pwsh", "no-gpu-sampler",
            "target-fps", "load-size", "load-timeout", "load-warmup", "load-exe", "overlay", "overlay-dot", "app-exe"],
        ["drift"] = [.. Common, .. Capture, "minutes", "tone-dbfs", "bucket-seconds", "burst-track", "reanalyze"],
        ["playback"] = [.. Common, .. Capture, "seconds", "players", "tone-dbfs", "auto", "chrome", "play-seconds", "player-timeout"],
        ["hotkey"] = [.. Common, "presses", "key", "timeout", "inject"],

        // Its Output Devices are --device-a and --device-b: --output-device doesn't apply.
        ["tracks"] = [.. Common, "size", "fps", "bitrate", "input-device", "minutes", "device-a", "device-b", "tone-dbfs", "vlc", "reanalyze"],
        ["overlay"] = [.. Common, "modes", "seconds", "settle", "presentmon", "target-fps", "load-size", "load-timeout", "load-warmup", "load-exe", "overlay-exe", "host", "app-exe", "dot-only", "overlay-dot"],
        ["soak"] = [.. Common, "minutes", "mode", "press-seconds", "buffer-seconds", "sample-seconds", "warmup-minutes", "key",
            "target-fps", "load-size", "load-exe", "app-exe", "lock", "kill-engine", "kill-app", "hang-engine"],
        ["report"] = [.. Common],
    };

    private static readonly HashSet<string> FlagNames = new(StringComparer.Ordinal) { "no-gpu-sampler", "reanalyze", "inject", "auto", "dot-only", "overlay" };

    private readonly Dictionary<string, string> _values;
    private readonly HashSet<string> _flags;

    private BenchOptions(string command, Dictionary<string, string> values, HashSet<string> flags)
    {
        Command = command;
        _values = values;
        _flags = flags;
    }

    public string Command { get; }

    public static IReadOnlyCollection<string> Commands => Allowed.Keys;

    /// <exception cref="BenchUsageException">An unknown command or option, a repeated option, or a missing value.</exception>
    public static BenchOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new BenchUsageException("No command given.");
        }

        var command = args[0];
        if (!Allowed.TryGetValue(command, out var allowed))
        {
            throw new BenchUsageException($"Unknown command '{command}'. Use one of {string.Join(", ", Allowed.Keys)}.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                throw new BenchUsageException($"Unexpected argument '{arg}'.");
            }

            var name = arg[2..];
            if (!allowed.Contains(name))
            {
                throw new BenchUsageException($"'{command}' has no option --{name}.");
            }

            if (values.ContainsKey(name) || flags.Contains(name))
            {
                throw new BenchUsageException($"--{name} is given twice.");
            }

            if (FlagNames.Contains(name))
            {
                flags.Add(name);
                continue;
            }

            if (i + 1 >= args.Count)
            {
                throw new BenchUsageException($"--{name} needs a value.");
            }

            values[name] = args[++i];
        }

        return new BenchOptions(command, values, flags);
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public bool Flag(string name) => _flags.Contains(name);

    public string? Text(string name) => _values.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    public string Text(string name, string fallback) => Text(name) ?? fallback;

    public int Int(string name, int fallback, int min, int max)
    {
        if (!_values.TryGetValue(name, out var text))
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new BenchUsageException($"--{name} '{text}' is not a whole number from {min} to {max}.");
        }

        return value;
    }

    public double Double(string name, double fallback, double min, double max)
    {
        if (!_values.TryGetValue(name, out var text))
        {
            return fallback;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new BenchUsageException($"--{name} '{text}' is not a number from {min} to {max}.");
        }

        return value;
    }

    /// <summary>A comma-separated list, trimmed, without empty items.</summary>
    public IReadOnlyList<string> List(string name, IReadOnlyList<string> fallback)
    {
        if (Text(name) is not { } text)
        {
            return fallback;
        }

        var items = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return items.Length == 0 ? throw new BenchUsageException($"--{name} lists nothing.") : items;
    }

    /// <summary>Parses <c>WxH</c>, each side even and from 2 to 8192.</summary>
    /// <exception cref="BenchUsageException">Not such a size.</exception>
    public static (int Width, int Height) ParseSize(string size, string option)
    {
        var parts = size.Split('x');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            || width is < 2 or > 8192 || height is < 2 or > 8192 || width % 2 != 0 || height % 2 != 0)
        {
            throw new BenchUsageException($"--{option} '{size}' is not WxH with even sides.");
        }

        return (width, height);
    }

    /// <summary>The capture options every <c>debug-capture</c> Bench starts gets.</summary>
    public CaptureSettings CaptureSettings()
    {
        var (width, height) = ParseSize(Text("size", "1920x1080"), "size");
        return new CaptureSettings(
            width,
            height,
            Int("fps", 60, 1, 120),
            Int("bitrate", 20_000, 1, 1_000_000),
            Text("output-device"),
            Text("input-device"));
    }
}

/// <summary>The engine's output for a Bench capture: H.264, CBR (the engine's defaults are the codec and rate control).</summary>
internal sealed record CaptureSettings(int Width, int Height, int Fps, int BitrateKbps, string? OutputDevice, string? InputDevice)
{
    public string Size => $"{Width}x{Height}";

    /// <summary>One video frame at the output rate, in ms: the drift target.</summary>
    public double FrameMs => 1000.0 / Fps;
}

/// <summary>Where Bench finds the repo, its tools and its run folder.</summary>
internal sealed class BenchContext
{
    public const string SolutionFile = "ReplayBuffer.slnx";

    private BenchContext(string repoRoot, string runFolder, BenchOptions options)
    {
        RepoRoot = repoRoot;
        RunFolder = runFolder;
        Options = options;
    }

    public string RepoRoot { get; }

    public string RunFolder { get; }

    public BenchOptions Options { get; }

    public static BenchContext Create(BenchOptions options)
    {
        var repo = FindRepoRoot(options.Text("repo"));
        var run = ResolveRunFolder(repo, options.Text("out"), options.Command == "report", DateTime.Now);
        Directory.CreateDirectory(run);
        return new BenchContext(repo, run, options);
    }

    /// <summary>
    /// <paramref name="given"/>, or the first folder holding <see cref="SolutionFile"/> above
    /// Bench's own folder, then above the current folder.
    /// </summary>
    public static string FindRepoRoot(string? given)
    {
        if (given is not null)
        {
            var full = Path.GetFullPath(given);
            return File.Exists(Path.Combine(full, SolutionFile))
                ? full
                : throw new BenchFailure($"--repo '{full}' has no {SolutionFile}.");
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, SolutionFile)))
                {
                    return dir.FullName;
                }
            }
        }

        throw new BenchFailure($"No {SolutionFile} above {AppContext.BaseDirectory} or {Environment.CurrentDirectory}; pass --repo.");
    }

    /// <summary>
    /// <paramref name="given"/>, or a new <c>measurements/bench-&lt;time&gt;</c>; for <c>report</c>,
    /// the newest existing <c>bench-*</c> folder.
    /// </summary>
    public static string ResolveRunFolder(string repo, string? given, bool forReport, DateTime now)
    {
        if (given is not null)
        {
            return Path.GetFullPath(given);
        }

        var measurements = Path.Combine(repo, "measurements");
        if (forReport)
        {
            var newest = Directory.Exists(measurements)
                ? new DirectoryInfo(measurements).GetDirectories("bench-*").OrderBy(d => d.Name, StringComparer.Ordinal).LastOrDefault()
                : null;
            return newest?.FullName ?? throw new BenchFailure($"No bench-* run folder in {measurements}; pass --out.");
        }

        return Path.Combine(measurements, "bench-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    }

    /// <summary><c>--engine</c>, or <see cref="FindEngine"/>.</summary>
    public string ResolveEngine()
    {
        if (Options.Text("engine") is { } given)
        {
            var full = Path.GetFullPath(given);
            return File.Exists(full) ? full : throw new BenchFailure($"The engine is not at '{full}'.");
        }

        return FindEngine(RepoRoot)
            ?? throw new BenchFailure("No Release engine build. Run dotnet build ReplayBuffer.slnx -c Release, or pass --engine <path>.");
    }

    /// <summary>
    /// The newer of the published engine (<c>publish/engine</c>) and the Release build's own folder
    /// (<c>bin/Release/&lt;tfm&gt;</c>; Debug skews its CPU and allocation figures). Never a folder under
    /// it, such as the NativeAOT compiler's <c>&lt;rid&gt;/native</c> output, which has no FFmpeg beside it.
    /// </summary>
    internal static string? FindEngine(string repo)
    {
        const string Exe = "ReplayBuffer.Engine.exe";
        var candidates = new List<FileInfo> { new(Path.Combine(repo, "publish", "engine", Exe)) };
        var release = new DirectoryInfo(Path.Combine(repo, "src", "ReplayBuffer.Engine", "bin", "Release"));
        if (release.Exists)
        {
            candidates.AddRange(release.GetDirectories().Select(tfm => new FileInfo(Path.Combine(tfm.FullName, Exe))));
        }

        return candidates.Where(f => f.Exists).OrderBy(f => f.LastWriteTimeUtc).LastOrDefault()?.FullName;
    }

    /// <summary><c>--ffmpeg</c>, or Bench's own folder (the build copies the natives there), or <c>native/ffmpeg</c>.</summary>
    public FfmpegTools ResolveFfmpeg() => FfmpegTools.Resolve(Options.Text("ffmpeg"), RepoRoot);

    /// <summary><c>--pwsh</c>, or <c>pwsh.exe</c> on the PATH.</summary>
    public string ResolvePwsh() =>
        Options.Text("pwsh") is { } given
            ? File.Exists(given) ? Path.GetFullPath(given) : throw new BenchFailure($"pwsh is not at '{given}'.")
            : FindOnPath("pwsh.exe") ?? throw new BenchFailure("pwsh.exe (PowerShell 7) is not on the PATH; install it or pass --pwsh <path>.");

    /// <summary><c>--presentmon</c>, or the newest <c>native/presentmon/PresentMon-*.exe</c>, as the FPS script picks it.</summary>
    public string ResolvePresentMon()
    {
        if (Options.Text("presentmon") is { } given)
        {
            return File.Exists(given) ? Path.GetFullPath(given) : throw new BenchFailure($"PresentMon not found at '{given}'.");
        }

        var folder = Path.Combine(RepoRoot, "native", "presentmon");
        var found = Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles("PresentMon-*.exe")
                .OrderBy(f => VersionOf(f.Name))
                .ThenBy(f => f.LastWriteTimeUtc)
                .LastOrDefault()
            : null;
        return found?.FullName ?? throw new BenchFailure(
            $"PresentMon not found: no native/presentmon/PresentMon-*.exe under {RepoRoot}. Download the PresentMon CLI there, or pass --presentmon <path to PresentMon.exe>.");
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static Version VersionOf(string fileName)
    {
        var text = Path.GetFileNameWithoutExtension(fileName)["PresentMon-".Length..];
        var end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'))
        {
            end++;
        }

        return Version.TryParse(text[..end].Trim('.'), out var version) ? version : new Version(0, 0);
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        return null;
    }
}

/// <summary>ffmpeg.exe and ffprobe.exe, side by side.</summary>
internal sealed record FfmpegTools(string Ffmpeg, string Ffprobe)
{
    public static FfmpegTools? Find(string? given, string? repoRoot)
    {
        var folders = given is not null
            ? [Path.GetFullPath(given)]
            : new[] { AppContext.BaseDirectory, repoRoot is null ? null : Path.Combine(repoRoot, "native", "ffmpeg") };
        foreach (var folder in folders.OfType<string>())
        {
            var ffmpeg = Path.Combine(folder, "ffmpeg.exe");
            var ffprobe = Path.Combine(folder, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
            {
                return new FfmpegTools(ffmpeg, ffprobe);
            }
        }

        return null;
    }

    public static FfmpegTools Resolve(string? given, string repoRoot) =>
        Find(given, repoRoot) ?? throw new BenchFailure(
            given is not null
                ? $"--ffmpeg '{given}' has no ffmpeg.exe and ffprobe.exe."
                : "ffmpeg.exe and ffprobe.exe are not beside Bench or in native/ffmpeg; run pwsh tools/fetch-ffmpeg.ps1, or pass --ffmpeg <folder>.");
}
