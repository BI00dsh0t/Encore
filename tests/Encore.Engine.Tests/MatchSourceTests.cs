using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Sessions;
using Encore.Core.Settings;
using Encore.Media;
using Encore.Engine.Sessions;
using Encore.Engine.Settings;
using Encore.Platform.Capture;

namespace Encore.Engine.Tests;

/// <summary>MatchSource through the settings and onto the game capture chain.</summary>
public sealed class MatchSourceTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-match-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Instance TestInstance => new(ServeCommand.TestInstancePrefix + Path.GetFileName(_folder), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            await disposable.DisposeAsync();
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void WithMatchSourceOnAGameChainTakesTheCapturedSizeAndKeepsTheSnapshotsFps()
    {
        var video = new VideoSettings(VideoCodec.H264, 1920, 1080, 60, 20_000, RateControl.Cbr, MatchSource: true);
        var on = Chain(video);
        var off = Chain(video with { MatchSource = false });
        var monitor = Chain(video, SessionKind.Monitor);
        var source = new VideoSize(2560, 1440);

        Assert.Equal((new VideoSize(2560, 1440), 60), (on.SizedToSource(isMonitor: false, source).OutputSize, on.Fps));
        Assert.Equal(new VideoSize(1920, 1080), off.SizedToSource(isMonitor: false, source).OutputSize);
        Assert.Equal(new VideoSize(1920, 1080), monitor.SizedToSource(isMonitor: true, source).OutputSize);
        Assert.Equal(new VideoSize(1920, 1080), Chain(video).SizedToSource(isMonitor: false, new VideoSize(600, 300)).OutputSize);
    }

    [Fact]
    public async Task AnOldSettingsFileLoadsAndMatchSourceRoundTripsThroughSettingsUpdate()
    {
        // A settings.json written before matchSource existed: its video has none.
        Directory.CreateDirectory(TestInstance.RoamingDataFolder);
        await File.WriteAllTextAsync(
            Path.Combine(TestInstance.RoamingDataFolder, SettingsStore.FileName),
            """{"schemaVersion":1,"revision":1,"global":{"video":{"codec":"h264","width":1280,"height":720,"fps":30,"bitrateKbps":8000,"rateControl":"cbr"}},"profiles":{}}""",
            Token);
        var client = await ConnectAsync(Start());
        var loaded = SettingsResolver.Resolve(client.State.Settings, null).Video;
        Assert.Equal((1280, 720, false), (loaded.Width, loaded.Height, loaded.MatchSource));

        var on = loaded with { MatchSource = true };
        await client.SendAsync(SettingsMessages.Update, new SettingsUpdate(client.State.Settings.Revision, [SettingsOp.SetGlobal(Keys.Video.Name, Keys.Video.ToJson(on))]), Token);
        var clock = Stopwatch.StartNew();
        while (!SettingsResolver.Resolve(client.State.Settings, null).Video.MatchSource)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "settings.changed with matchSource");
            await Task.Delay(20, Token);
        }

        Assert.Equal(on, SettingsResolver.Resolve(client.State.Settings, null).Video);
    }

    /// <summary>The real path (at night only): a 2560×1440 window recorded at its own size for 30 s; needs a screen that large.</summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ARealCaptureOfA2560By1440WindowWithMatchSourceRecordsAt2560By1440()
    {
        using var bench = BenchLock.Take("Engine 12.1 match-source capture test");
        SessionTestKit.RequireFFmpeg();
        var processes = new List<Process>();
        try
        {
            var window = SessionTestKit.OpenWindow(_folder, processes, BigWindowScript);
            var output = Path.Combine(_folder, "match.mp4");
            using var engine = Process.Start(new ProcessStartInfo(Path.Combine(EngineBinaries.Folder, "Encore.Engine.exe"))
            {
                ArgumentList = { "debug-capture", "--hwnd", $"0x{window.Handle:X}", "--seconds", "30", "--fps", "60", "--size", "1280x720", "--match-source", "--output", output, "--instance", TestInstance.Name },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var log = engine.StandardOutput.ReadToEndAsync(Token);
            var errors = engine.StandardError.ReadToEndAsync(Token);
            await engine.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(90), Token);

            Assert.True(engine.ExitCode == 0, await errors);
            Assert.Contains("output 2560x1440", await log, StringComparison.Ordinal);
            var video = SessionTestKit.VideoStream(output);
            Assert.Equal((2560, 1440), (video.Width, video.Height));

            // The Output frame rate (60), for the whole 30 s: not 30 fps, and not a file cut short.
            var pts = SessionTestKit.VideoPts(output);
            var seconds = pts[^1] - pts[0];
            Assert.InRange(seconds, 29, 31);
            Assert.InRange((pts.Count - 1) / seconds, 59, 61);
            SessionTestKit.AssertDecodesCleanly(output);
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                process.Dispose();
            }
        }
    }

    // A borderless, animated 2560x1440 window at the primary screen's top-left.
    private const string BigWindowScript =
        """
        param([string] $Title)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.FormBorderStyle = 'None'
        $form.StartPosition = 'Manual'
        $form.Left = 0
        $form.Top = 0
        $form.Width = 2560
        $form.Height = 1440
        $start = [Diagnostics.Stopwatch]::StartNew()
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 16
        $timer.Add_Tick({
            $v = [int](($start.ElapsedMilliseconds / 4) % 255)
            $form.BackColor = [Drawing.Color]::FromArgb($v, 255 - $v, 128)
        })
        $timer.Start()
        [void] $form.ShowDialog()
        """;

    private static EncoderChain Chain(VideoSettings video, SessionKind kind = SessionKind.Game)
    {
        var snapshot = SettingsResolver.Resolve(SettingsDocument.Empty with { Global = SettingsDocument.Empty.Global.SetItem(Keys.Video.Name, Keys.Video.ToJson(video)) }, null);
        var spec = new SessionSpec(kind, new CaptureTarget(1, 0, null), snapshot, SessionAudio.None);
        return new EncoderChain(spec, "h264_nvenc", VideoCodec.H264, null, new CaptureSessionOptions { OutputFolder = Path.GetTempPath(), Replay = true }, null!, null!, new MicMuteSchedule(true), NullLoggerFactory.Instance, NullLogger.Instance, _ => { });
    }

    private EngineHost Start()
    {
        var host = EngineHost.Start(TestInstance, NullLoggerFactory.Instance, hostOptions: new EngineHostOptions
        {
            Heartbeat = null,
            ProbeEncoders = false,
            AudioEndpoints = new FakeEndpointSource(),
            GameWindows = new GameDetectionServeTests.FakeProbe(),
        });
        _disposables.Add(host);
        return host;
    }

    private async Task<EngineClient> ConnectAsync(EngineHost host)
    {
        var client = await EngineClient.ConnectAsync(host.PipeName, new EngineClientOptions(), Token);
        _disposables.Add(client);
        return client;
    }
}
