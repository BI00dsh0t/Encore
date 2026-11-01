using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Bench;

/// <summary>One press: the engine's stamp minus the hook's, ms.</summary>
internal sealed record HotkeyPressTiming(int Press, double Ms, bool Injected);

/// <summary><c>hotkey.json</c>.</summary>
/// <param name="Injected">True when Bench pressed the key with <c>SendInput</c> (<c>--inject</c>); null in a file written before it was recorded.</param>
internal sealed record HotkeyReport(
    DateTimeOffset Created,
    string Key,
    int PressesRequested,
    int Presses,
    int Unpaired,
    IReadOnlyList<HotkeyPressTiming> Timings,
    double? P50Ms,
    double? P99Ms,
    double? MaxMs,
    double? MeanMs,
    double TargetMs,
    double OldBarMs,
    string Verdict,
    string? Error,
    bool? Injected = null);

/// <summary>
/// <c>bench hotkey</c>: real presses of a Save Hotkey bound on a throwaway <c>serve</c> instance. A
/// <c>WH_KEYBOARD_LL</c> hook in Bench stamps each key-down; the engine stamps its <c>WM_HOTKEY</c> and
/// publishes <c>hotkey.pressed</c>. Press-to-stamp is the difference of the two QPCs. With <c>--inject</c>,
/// Bench presses the combination itself with <c>SendInput</c> about once a second, for unattended runs.
/// </summary>
internal static class HotkeyBenchmark
{
    public const string FileName = "hotkey.json";
    public const double TargetMs = 1000;
    public const double OldBarMs = 1;

    public static int Run(BenchContext context)
    {
        var options = context.Options;
        var presses = options.Int("presses", 20, 1, 1000);
        var combo = KeyCombo.Parse(options.Text("key", "ctrl+alt+f9"));
        var timeout = TimeSpan.FromSeconds(options.Int("timeout", 60, 5, 3600));
        var inject = options.Flag("inject");
        var engine = context.ResolveEngine();
        var instance = Instance.TestPrefix + "bench-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(context.RunFolder, FileName);

        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        Process? process = null;
        try
        {
            WriteHotkeys(instance, new HotkeySettings(new HotkeyBinding(combo.Vk, combo.Modifiers), null, null, null, null, null));
            process = StartServe(engine, instance, Path.Combine(context.RunFolder, "hotkey"), cancel.Token);
            var events = Channel.CreateUnbounded<HotkeyPress>();
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            var client = EngineClient.ConnectAsync(
                PipeName.ForCurrentUser(instance),
                new EngineClientOptions
                {
                    OnEvent = e =>
                    {
                        if (e.Type == HotkeyMessages.Pressed.Name)
                        {
                            events.Writer.TryWrite(HotkeyMessages.Pressed.Read(e.Payload));
                        }
                    },
                },
                connect.Token).GetAwaiter().GetResult();
            try
            {
                var save = client.State.Hotkeys.Bindings.First(b => b.Action == HotkeyAction.Save);
                if (!save.Registered)
                {
                    var why = save.Code == ErrorCodes.HotkeyTaken
                        ? $"{ErrorCodes.HotkeyTaken}: another process holds {combo.Text}; pick another --key"
                        : $"{save.Code}: the engine did not register {combo.Text}";
                    BenchJson.Write(path, Result(combo, presses, [], 0, why, inject));
                    Console.Error.WriteLine(why);
                    return 1;
                }

                var timings = Measure(combo, presses, timeout, events.Reader, inject, out var unpaired, out var injectFault, cancel.Token);
                var report = Result(combo, presses, timings, unpaired, injectFault, inject);
                BenchJson.Write(path, report);
                if (injectFault is not null)
                {
                    Console.Error.WriteLine(injectFault);
                    return 1;
                }

                Console.WriteLine($"Hotkey: {timings.Count} press(es){(inject ? " injected" : "")}; p50 {Format(report.P50Ms)} ms, p99 {Format(report.P99Ms)} ms, max {Format(report.MaxMs)} ms ({report.Verdict}).");
            }
            finally
            {
                // Shut the test engine down on every path, so it lets go of the combination at once.
                try
                {
                    client.SendAsync(EngineMessages.Shutdown, Empty.Value, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
                catch (Exception e) when (e is IOException or TimeoutException or ErrorCodeException or ObjectDisposedException)
                {
                    // Already gone; the outer finally kills it if it isn't.
                }

                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            return 0;
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException)
        {
            // serve never said it was listening, the pipe didn't connect, or Ctrl+C: a clean exit 1, not a crash.
            throw new BenchFailure(e is OperationCanceledException
                ? "Stopped before the Hotkey run started."
                : $"Could not start or reach the test engine: {e.Message}");
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            if (process is not null)
            {
                if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }

                process.Dispose();
            }

            foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
            {
                var folder = Path.Combine(Environment.GetFolderPath(root), instance);
                if (Directory.Exists(folder))
                {
                    try
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                    catch (IOException)
                    {
                        Console.Error.WriteLine($"Could not remove {folder}.");
                    }
                }
            }
        }
    }

    /// <summary>The report of <paramref name="timings"/>, each press held to <see cref="TargetMs"/>.</summary>
    internal static HotkeyReport Result(KeyCombo combo, int requested, List<HotkeyPressTiming> timings, int unpaired, string? error, bool injected = false)
    {
        var ms = timings.Select(t => t.Ms).ToList();
        string verdict = error is not null || ms.Count == 0
            ? DriftMath.Inconclusive
            : ms.Any(m => m >= TargetMs) ? DriftMath.Miss
            : DriftMath.Pass;
        return new HotkeyReport(
            DateTimeOffset.Now,
            combo.Text,
            requested,
            ms.Count,
            unpaired,
            timings,
            ms.Count > 0 ? Math.Round(DriftMath.Percentile(ms, 50), 3) : null,
            ms.Count > 0 ? Math.Round(DriftMath.Percentile(ms, 99), 3) : null,
            ms.Count > 0 ? Math.Round(ms.Max(), 3) : null,
            ms.Count > 0 ? Math.Round(ms.Average(), 3) : null,
            TargetMs,
            OldBarMs,
            verdict,
            error ?? (ms.Count < requested && ms.Count > 0 ? $"only {ms.Count} of {requested} presses arrived" : null),
            injected);
    }

    private static List<HotkeyPressTiming> Measure(
        KeyCombo combo, int presses, TimeSpan timeout, ChannelReader<HotkeyPress> events, bool inject, out int unpaired, out string? injectFault, CancellationToken cancellation)
    {
        using var hook = new KeyboardHook(combo.Vk);
        var timings = new List<HotkeyPressTiming>();
        var downs = new List<(long Qpc, bool Injected)>();
        unpaired = 0;
        injectFault = null;
        if (inject)
        {
            Console.WriteLine($"Injecting {combo.Text} with SendInput about once a second, {presses} times. Each press saves nothing on this test instance.");
        }
        else
        {
            Console.WriteLine($"Press {combo.Text} {presses} times, about a second apart, with the game focused. Each press saves nothing on this test instance.");
        }

        Console.WriteLine($"Stops after {presses} presses, or {timeout.TotalSeconds:0} s without one, or Ctrl+C.");

        // The hook is in place before the first injected press; a press the engine misses is pressed again,
        // up to twice the count asked for.
        using var injector = inject ? KeyInjector.Start(combo, TimeSpan.FromSeconds(1), presses * 2) : null;
        while (timings.Count < presses && !cancellation.IsCancellationRequested)
        {
            HotkeyPress press;
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation, injector?.Faulted ?? CancellationToken.None);
                wait.CancelAfter(timeout);
                press = events.ReadAsync(wait.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                if (injector?.Fault is { } fault)
                {
                    injectFault = fault;
                    Console.WriteLine("The key injector stopped: " + fault);
                    break;
                }

                Console.WriteLine(cancellation.IsCancellationRequested ? "Stopped." : $"No press for {timeout.TotalSeconds:0} s; stopping with what was gathered.");
                break;
            }

            if (press.Action != HotkeyAction.Save)
            {
                continue;
            }

            // The key-down this WM_HOTKEY came from is the latest one stamped before it.
            downs.AddRange(hook.Take());
            var before = downs.Where(d => d.Qpc <= press.Qpc).ToList();
            if (before.Count == 0)
            {
                unpaired++;
                Console.WriteLine("  a press arrived with no key-down stamped before it; not counted.");
                continue;
            }

            var down = before[^1];
            downs.RemoveAll(d => d.Qpc <= press.Qpc);
            var ms = (press.Qpc - down.Qpc) * 1000.0 / Stopwatch.Frequency;
            timings.Add(new HotkeyPressTiming(timings.Count + 1, Math.Round(ms, 4), down.Injected));
            Console.WriteLine($"  press {timings.Count}: {ms.ToString("0.000", CultureInfo.InvariantCulture)} ms{(down.Injected ? " (injected)" : "")}");
        }

        return timings;
    }

    private static string Format(double? value) => value?.ToString("0.000", CultureInfo.InvariantCulture) ?? "n/a";

    private static void WriteHotkeys(string instance, HotkeySettings hotkeys)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), instance);
        var document = SettingsDocument.Empty with { Global = SettingsMaps.EmptyValues.Add(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(hotkeys)) };
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), JsonSerializer.Serialize(document, SettingsJsonContext.Default.SettingsDocument));
    }

    /// <summary>Starts <c>serve</c> on the test instance and waits for its <c>listening</c> line; its output goes to log files.</summary>
    private static Process StartServe(string engine, string instance, string logPrefix, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(engine)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--instance");
        start.ArgumentList.Add(instance);
        var stdout = new StreamWriter(logPrefix + "-engine.out.log") { AutoFlush = true };
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new BenchFailure($"Could not start {engine}.");
        }
        catch
        {
            stdout.Dispose();
            throw;
        }

        try
        {
            var expected = "listening " + PipeName.ForCurrentUser(instance);
            string? line;
            do
            {
                line = process.StandardOutput.ReadLineAsync(cancellation).AsTask().WaitAsync(TimeSpan.FromSeconds(30), cancellation).GetAwaiter().GetResult();
                if (line is not null)
                {
                    stdout.WriteLine(line);
                }
            }
            while (line is not null && !line.StartsWith("listening ", StringComparison.Ordinal));

            if (line != expected)
            {
                Stop(process);
                throw new BenchFailure($"serve did not start listening on {expected}: {process.StandardError.ReadToEnd().Trim()}");
            }
        }
        catch
        {
            // Whatever went wrong after the start (a timeout, Ctrl+C, a wrong pipe), the engine must not keep the Hotkey.
            Stop(process);
            stdout.Dispose();
            process.Dispose();
            throw;
        }

        // Keep reading, so the engine never blocks on a full pipe.
        _ = Task.Run(async () =>
        {
            using (stdout)
            {
                while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } next)
                {
                    stdout.WriteLine(next);
                }
            }
        });
        _ = Task.Run(async () => await File.WriteAllTextAsync(logPrefix + "-engine.err.log", await process.StandardError.ReadToEndAsync(CancellationToken.None)));
        return process;
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // It exited in between.
        }
    }
}
