using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Settings;
using Encore.Core.Sessions;
using Encore.Engine.Sessions;
using Encore.Platform.Audio;
using Encore.Platform.Windows;
using PlatformFlow = Encore.Platform.Audio.AudioFlow;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Engine.Tests;

/// <summary>A capture session driven by orchestrator messages: started, retargeted and ended, with one unbroken set of streams.</summary>
public sealed class SessionCaptureTests : IDisposable
{
    private const string WindowsScript =
        """
        param([string] $Title, [int] $Count)
        Add-Type -AssemblyName System.Windows.Forms
        $start = [Diagnostics.Stopwatch]::StartNew()
        $forms = @()
        for ($i = 0; $i -lt $Count; $i++) {
            $form = New-Object System.Windows.Forms.Form
            $form.Text = "$Title-$i"
            $form.Width = 641
            $form.Height = 481
            $form.StartPosition = 'Manual'
            $form.Left = 40 + 680 * $i
            $form.Top = 40
            $forms += $form
        }
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 16
        $timer.Add_Tick({
            $v = [int](($start.ElapsedMilliseconds / 4) % 255)
            for ($i = 0; $i -lt $forms.Count; $i++) {
                if ($i % 2 -eq 0) { $forms[$i].BackColor = [Drawing.Color]::FromArgb($v, 255 - $v, 128) }
                else { $forms[$i].BackColor = [Drawing.Color]::FromArgb(128, $v, 255 - $v) }
            }
        })
        $timer.Start()
        # The hidden launch's SW_HIDE applies to the first window shown: spend it on a throwaway show.
        $forms[0].Show()
        $forms[0].Hide()
        for ($i = 1; $i -lt $forms.Count; $i++) { $forms[$i].Show() }
        [System.Windows.Forms.Application]::Run($forms[0])
        """;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-session-").FullName;
    private readonly List<Process> _processes = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            process.Dispose();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ARetargetedSessionWritesOneFileWithFramesFromBothWindows()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(EngineBinaries.Folder, "ffprobe.exe")), "native/ffmpeg is not fetched");
        var windows = OpenWindows(count: 2);
        var other = OpenWindows(count: 1)[0];
        Assert.Equal(windows[0].ProcessId, windows[1].ProcessId);
        Assert.NotEqual(windows[0].ProcessId, other.ProcessId);

        var output = Path.Combine(_directory, "session.mp4");
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new CaptureSessionOptions
        {
            OutputFolder = _directory,
            OutputPath = output,
            Anchored = qpc => anchored.TrySetResult(qpc),
        };
        using var hub = new AudioDeviceHub(NullLoggerFactory.Instance);
        using var orchestrator = new SessionOrchestrator(
            new CaptureSessionFactory(options, NullLoggerFactory.Instance, hub), () => SettingsDocument.Empty, _ => { });

        var running = await orchestrator.StartAsync(SessionKind.Game, Target(windows[0]), DefaultAudio()).WaitAsync(Timeout, Token);
        var anchorQpc = await anchored.Task.WaitAsync(Timeout, Token);
        await Task.Delay(TimeSpan.FromSeconds(2), Token);

        var refused = await Assert.ThrowsAsync<ErrorCodeException>(() => orchestrator.RetargetAsync(Target(other)).WaitAsync(Timeout, Token));
        Assert.Equal(ErrorCodes.RetargetOtherProcess, refused.Code);

        var switchedAt = (double)(Stopwatch.GetTimestamp() - anchorQpc) / Stopwatch.Frequency;
        var spec = await orchestrator.RetargetAsync(Target(windows[1])).WaitAsync(Timeout, Token);
        Assert.Equal(windows[1].Handle, spec.Target.Window);
        Assert.Same(running.Spec.Snapshot, spec.Snapshot);
        await Task.Delay(TimeSpan.FromSeconds(2), Token);

        var outcome = await orchestrator.EndAsync("test over").WaitAsync(Timeout, Token);
        Assert.Null(outcome.Failure);
        Assert.Equal(output, outcome.Path);
        Assert.Null((await running.Ended.WaitAsync(Timeout, Token)).Failure);

        Assert.Equal(["h264", "aac", "aac"], Probe(output, "-show_streams").GetProperty("streams").EnumerateArray()
            .Select(s => s.GetProperty("codec_name").GetString()));

        var pts = Probe(output, "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-show_packets")
            .GetProperty("packets").EnumerateArray()
            .Select(p => double.Parse(p.GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture))
            .ToList();
        for (var i = 1; i < pts.Count; i++)
        {
            Assert.True(pts[i] > pts[i - 1], $"video PTS {pts[i]} follows {pts[i - 1]}");
        }

        // Frames from the first window before the switch, and from the second well after it.
        Assert.Contains(pts, t => t < switchedAt - 0.5);
        Assert.Contains(pts, t => t > switchedAt + 1);
    }

    private static CaptureTarget Target(WindowInfo window) => new((int)window.ProcessId, window.Handle, ProfileId: null);

    private static SessionAudio DefaultAudio()
    {
        var output = AudioEndpoints.Default(PlatformFlow.Output);
        var input = AudioEndpoints.Default(PlatformFlow.Input);
        return SessionAudio.OnePerDevice(
        [
            new SessionAudioDevice(output.Id, SettingsFlow.Output, output.FriendlyName),
            new SessionAudioDevice(input.Id, SettingsFlow.Input, input.FriendlyName),
        ]);
    }

    private List<WindowInfo> OpenWindows(int count)
    {
        var title = "RB-Session-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(_directory, title + ".ps1");
        File.WriteAllText(path, WindowsScript);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-File", path, "-Title", title, "-Count", count.ToString(CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(arg);
        }

        _processes.Add(Process.Start(start)!);

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            var found = Enumerable.Range(0, count)
                .Select(i => WindowFinder.List().FirstOrDefault(w => w.Title == $"{title}-{i}"))
                .ToList();
            if (found.All(w => w is not null))
            {
                return found!;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException("The test windows never appeared.");
    }

    private static JsonElement Probe(string file, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(EngineBinaries.Folder, "ffprobe.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "error", "-of", "json" }.Concat(args).Append(file))
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);

        using var json = JsonDocument.Parse(stdout.Result);
        return json.RootElement.Clone();
    }
}
