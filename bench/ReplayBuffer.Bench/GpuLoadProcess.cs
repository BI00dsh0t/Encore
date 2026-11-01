using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ReplayBuffer.Bench;

/// <summary>
/// What <c>bench fps</c> measured: the synthetic load (<see cref="Kind"/> <c>synthetic</c>, with its window,
/// target and calibrated rates, iterations and adapter) or a real game (<c>game</c>, its process name only).
/// </summary>
internal sealed record GpuLoadInfo(
    string Kind,
    string Process,
    int? Width = null,
    int? Height = null,
    double? TargetFps = null,
    double? CalibratedFps = null,
    long? Iterations = null,
    string? Adapter = null,
    bool? Tearing = null)
{
    public const string Synthetic = "synthetic";
    public const string Game = "game";

    public static GpuLoadInfo RealGame(string process) => new(Game, process);
}

/// <summary>
/// <c>ReplayBuffer.Bench.GpuLoad.exe</c>, started for one <c>bench fps</c> run: Bench waits for its one
/// <c>ready</c> line, and stops it on every path (WM_CLOSE, then a kill). The load also exits by itself
/// when Bench does (<c>--parent</c>).
/// </summary>
internal sealed class GpuLoadProcess : IDisposable
{
    /// <summary>The load's process name, which the FPS script finds as <c>-Game</c> and PresentMon traces.</summary>
    public const string ProcessName = "ReplayBuffer.Bench.GpuLoad";
    public const string ExeName = ProcessName + ".exe";
    public const double DefaultTargetFps = 80;

    private const string ReadyPrefix = "ready ";
    private const int NoHardwareGpuExit = 3;

    private readonly Process _process;
    private bool _stopped;
    private bool _exited;

    private readonly StringBuilder _stderr;

    private GpuLoadProcess(Process process, GpuLoadInfo info, nint hwnd, StringBuilder stderr)
    {
        _process = process;
        _stderr = stderr;
        Info = info;
        Hwnd = hwnd;
    }

    public GpuLoadInfo Info { get; }

    public nint Hwnd { get; }

    public int Id => _process.Id;

    /// <summary>True once the load has exited, by itself or stopped by <see cref="Dispose"/>.</summary>
    public bool HasExited => _stopped ? _exited : _process.HasExited;

    /// <summary>What the load wrote on stderr so far.</summary>
    public string Stderr
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString().Trim();
            }
        }
    }

    /// <summary>
    /// For a load that exited by itself before <see cref="Dispose"/>: its exit code and stderr, once its output has
    /// drained.
    /// </summary>
    public string ExitDescription()
    {
        _process.WaitForExit();
        var stderr = Stderr;
        return $"exit {_process.ExitCode.ToString(CultureInfo.InvariantCulture)}{(stderr.Length > 0 ? ": " + stderr : ", nothing on stderr")}";
    }

    /// <summary>The ids of the <see cref="ProcessName"/> processes running now.</summary>
    public static List<int> Running()
    {
        var running = Process.GetProcessesByName(ProcessName);
        try
        {
            return [.. running.Select(p => p.Id)];
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Starts the load and waits up to <paramref name="timeout"/> for its <c>ready</c> line.
    /// </summary>
    /// <exception cref="BenchFailure">It exited early or never said ready: its stderr, and it is stopped.</exception>
    public static GpuLoadProcess Start(string exe, double targetFps, string? size, double? warmupSeconds, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--target-fps");
        start.ArgumentList.Add(targetFps.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--parent");
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (size is not null)
        {
            start.ArgumentList.Add("--size");
            start.ArgumentList.Add(size);
        }

        if (warmupSeconds is { } warmup)
        {
            start.ArgumentList.Add("--warmup");
            start.ArgumentList.Add(warmup.ToString(CultureInfo.InvariantCulture));
        }

        Console.WriteLine($"GPU load: {exe} {string.Join(' ', start.ArgumentList)}");
        var process = Process.Start(start) ?? throw new BenchFailure($"Could not start {exe}.");
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };
        process.BeginErrorReadLine();

        string? failure = null;
        string? line = null;
        try
        {
            var read = process.StandardOutput.ReadLineAsync();
            while (true)
            {
                if (!read.Wait(timeout))
                {
                    failure = $"The GPU load did not say ready within {timeout.TotalSeconds:0} s";
                    break;
                }

                line = read.Result;
                if (line is null)
                {
                    process.WaitForExit(5000);
                    failure = process.HasExited && process.ExitCode == NoHardwareGpuExit
                        ? "The GPU load found no hardware GPU"
                        : $"The GPU load exited before it was ready (exit {(process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "?")})";
                    break;
                }

                if (line.StartsWith(ReadyPrefix, StringComparison.Ordinal))
                {
                    break;
                }

                read = process.StandardOutput.ReadLineAsync();
            }
        }
        catch (Exception e) when (e is AggregateException or IOException)
        {
            failure = $"The GPU load's output could not be read: {e.Message}";
        }

        GpuLoadReady? ready = null;
        if (failure is null)
        {
            try
            {
                ready = ParseReady(line!);
            }
            catch (FormatException e)
            {
                failure = $"The GPU load's ready line is malformed ({e.Message}): {line}";
            }
        }

        if (failure is not null)
        {
            Stop(process, 0);
            string text;
            lock (stderr)
            {
                text = stderr.ToString().Trim();
            }

            process.Dispose();
            throw new BenchFailure(text.Length > 0 ? $"{failure}: {text}" : failure + ".");
        }

        // Keep draining stdout, so the load never blocks on a full pipe.
        _ = process.StandardOutput.ReadToEndAsync();
        var info = new GpuLoadInfo(GpuLoadInfo.Synthetic, ProcessName, ready!.Width, ready.Height, ready.TargetFps, ready.CalibratedFps, ready.Iterations, ready.Adapter, ready.Tearing);
        Console.WriteLine($"GPU load ready: {info.Width}x{info.Height} on {info.Adapter}, {info.CalibratedFps:0.##} fps (target {info.TargetFps:0.##}) at {info.Iterations} iterations; window {ready.HwndText}.");
        return new GpuLoadProcess(process, info, ready.Hwnd, stderr);
    }

    /// <summary>Parses the load's <c>ready {json}</c> line.</summary>
    /// <exception cref="FormatException">Not a ready line, or one missing a field.</exception>
    internal static GpuLoadReady ParseReady(string line)
    {
        if (!line.StartsWith(ReadyPrefix, StringComparison.Ordinal))
        {
            throw new FormatException("it does not start with 'ready '");
        }

        try
        {
            using var document = JsonDocument.Parse(line[ReadyPrefix.Length..]);
            var root = document.RootElement;
            var hwndText = Required(root, "hwnd").GetString() ?? "";
            if (!hwndText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !long.TryParse(hwndText.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hwnd) || hwnd == 0)
            {
                throw new FormatException($"hwnd '{hwndText}' is not 0x<hex>");
            }

            return new GpuLoadReady(
                (nint)hwnd,
                hwndText,
                Required(root, "width").GetInt32(),
                Required(root, "height").GetInt32(),
                Required(root, "targetFps").GetDouble(),
                Required(root, "calibratedFps").GetDouble(),
                Required(root, "iterations").GetInt64(),
                Required(root, "adapter").GetString() ?? "",
                root.TryGetProperty("tearing", out var tearing) && tearing.ValueKind is JsonValueKind.True or JsonValueKind.False ? tearing.GetBoolean() : null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new FormatException(e.Message, e);
        }
    }

    /// <summary>
    /// <c>--load-exe</c>; else the load built beside Bench's own configuration; else the newest Release build.
    /// </summary>
    public static string Resolve(BenchContext context)
    {
        if (context.Options.Text("load-exe") is { } given)
        {
            var full = Path.GetFullPath(given);
            return File.Exists(full) ? full : throw new BenchFailure($"The GPU load is not at '{full}'.");
        }

        var bin = Path.Combine(context.RepoRoot, "bench", ProcessName, "bin");
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (tfm.Parent?.Name is { } configuration)
        {
            var sibling = Path.Combine(bin, configuration, tfm.Name, ExeName);
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        var release = Path.Combine(bin, "Release");
        var built = Directory.Exists(release)
            ? new DirectoryInfo(release).GetFiles(ExeName, SearchOption.AllDirectories).OrderBy(f => f.LastWriteTimeUtc).LastOrDefault()
            : null;
        return built?.FullName ?? throw new BenchFailure(
            $"No build of the GPU load ({ExeName}). Run dotnet build ReplayBuffer.slnx -c Release, or pass --load-exe <path>.");
    }

    /// <summary>Asks the load to close (WM_CLOSE), then kills it if it is still running.</summary>
    public void Dispose()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        Stop(_process, Hwnd);
        try
        {
            _exited = _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            _exited = true;
        }

        _process.Dispose();
    }

    private static void Stop(Process process, nint hwnd)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (hwnd != 0 && PostMessage(hwnd, WmClose, 0, 0) && process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It exited in between.
        }
    }

    private static JsonElement Required(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new FormatException($"it has no {name}");

    private const int WmClose = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
}

/// <summary>The load's ready line.</summary>
internal sealed record GpuLoadReady(
    nint Hwnd, string HwndText, int Width, int Height, double TargetFps, double CalibratedFps, long Iterations, string Adapter, bool? Tearing);
