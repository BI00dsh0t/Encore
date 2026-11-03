using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace ReplayBuffer.Bench;

/// <summary>
/// <c>ReplayBuffer.Bench.Overlay.exe</c>, story 5.1's Overlay window hosted on its own for <c>bench overlay</c>:
/// Bench waits for its <c>ready</c> line, shows and hides the dot over stdin, and stops it on every path.
/// </summary>
internal sealed class OverlayHostProcess : IDisposable
{
    public const string ProcessName = "ReplayBuffer.Bench.Overlay";
    public const string ExeName = ProcessName + ".exe";

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    private readonly Process _process;
    private readonly StringBuilder _stderr;
    private bool _disposed;

    private OverlayHostProcess(Process process, OverlayHostReady ready, StringBuilder stderr)
    {
        _process = process;
        _stderr = stderr;
        Ready = ready;
    }

    public OverlayHostReady Ready { get; }

    public nint Hwnd => Ready.Hwnd;

    /// <exception cref="BenchFailure">It exited early or never said ready: its stderr, and it is stopped.</exception>
    public static OverlayHostProcess Start(string exe, int gameProcessId)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--pid");
        start.ArgumentList.Add(gameProcessId.ToString(CultureInfo.InvariantCulture));
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

        OverlayHostReady? ready = null;
        string? failure = null;
        try
        {
            var line = ReadLine(process);
            ready = line is null ? null : ParseReady(line);
            failure = ready is null ? $"The Overlay host did not say ready (it said '{line ?? "nothing"}')" : null;
        }
        catch (Exception e) when (e is FormatException or TimeoutException)
        {
            failure = $"The Overlay host's ready line is unusable: {e.Message}";
        }

        if (failure is not null)
        {
            Stop(process);
            string text;
            lock (stderr)
            {
                text = stderr.ToString().Trim();
            }

            process.Dispose();
            throw new BenchFailure(text.Length > 0 ? $"{failure}: {text}" : failure + ".");
        }

        Console.WriteLine($"Overlay host ready: window 0x{ready!.Hwnd:X}, excluded from capture {ready.Excluded}, extended style 0x{ready.ExtendedStyle:X}.");
        return new OverlayHostProcess(process, ready, stderr);
    }

    /// <summary>Parses <c>ready 0x&lt;hwnd&gt; excluded=&lt;bool&gt; exstyle=0x&lt;hex&gt;</c>.</summary>
    /// <exception cref="FormatException">It is not that line.</exception>
    internal static OverlayHostReady ParseReady(string line)
    {
        var parts = line.Split(' ');
        if (parts is not ["ready", var hwnd, var excluded, var style]
            || !TryHex(hwnd, out var handle) || handle == 0
            || excluded is not ("excluded=true" or "excluded=false")
            || !style.StartsWith("exstyle=", StringComparison.Ordinal) || !TryHex(style["exstyle=".Length..], out var exStyle))
        {
            throw new FormatException($"'{line}' is not 'ready 0x<hwnd> excluded=<bool> exstyle=0x<hex>'");
        }

        return new OverlayHostReady((nint)handle, excluded == "excluded=true", (uint)exStyle);
    }

    public void ShowDot() => Send("show", "shown");

    public void HideDot() => Send("hide", "hidden");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _process.StandardInput.WriteLine("quit");
            _process.StandardInput.Flush();
        }
        catch (IOException)
        {
            // It exited already.
        }

        if (!_process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            Stop(_process);
        }

        _process.Dispose();
    }

    private void Send(string command, string reply)
    {
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
        var line = ReadLine(_process);
        if (line != reply)
        {
            string text;
            lock (_stderr)
            {
                text = _stderr.ToString().Trim();
            }

            throw new BenchFailure($"The Overlay host answered '{line ?? "nothing"}' to {command}{(text.Length > 0 ? ": " + text : "")}.");
        }
    }

    private static string? ReadLine(Process process)
    {
        var read = process.StandardOutput.ReadLineAsync();
        return read.Wait(ReplyTimeout) ? read.Result : throw new TimeoutException($"No answer within {ReplyTimeout.TotalSeconds:0} s.");
    }

    private static bool TryHex(string text, out long value)
    {
        value = 0;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It exited in between.
        }
    }

    /// <summary><c>--overlay-exe</c>; else the host built beside Bench's own configuration; else the newest Release build.</summary>
    public static string Resolve(BenchContext context)
    {
        if (context.Options.Text("overlay-exe") is { } given)
        {
            var full = Path.GetFullPath(given);
            return File.Exists(full) ? full : throw new BenchFailure($"The Overlay host is not at '{full}'.");
        }

        var bin = Path.Combine(context.RepoRoot, "bench", ProcessName, "bin");
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (tfm.Parent?.Name is { } configuration && Path.Combine(bin, configuration, tfm.Name, ExeName) is var sibling && File.Exists(sibling))
        {
            return sibling;
        }

        var release = Path.Combine(bin, "Release");
        var built = Directory.Exists(release)
            ? new DirectoryInfo(release).GetFiles(ExeName, SearchOption.AllDirectories).OrderBy(f => f.LastWriteTimeUtc).LastOrDefault()
            : null;
        return built?.FullName ?? throw new BenchFailure(
            $"No build of the Overlay host ({ExeName}). Run dotnet build ReplayBuffer.slnx -c Release, or pass --overlay-exe <path>.");
    }
}

/// <summary>The Overlay host's ready line: its window, whether it is excluded from capture, and its extended style.</summary>
internal sealed record OverlayHostReady([property: JsonIgnore] nint Hwnd, bool Excluded, uint ExtendedStyle);
