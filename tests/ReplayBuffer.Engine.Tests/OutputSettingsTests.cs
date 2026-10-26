using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Sessions;
using ReplayBuffer.Platform.Audio;
using ReplayBuffer.Platform.FFmpeg;
using ReplayBuffer.Platform.FFmpeg.Tests;
using ReplayBuffer.Platform.Windows;
using PlatformFlow = ReplayBuffer.Platform.Audio.AudioFlow;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// Runs <see cref="OutputSettingsTests"/> alone: the frame rate it measures at 3440×1440 and 120 fps
/// would suffer from the encode tests running beside it.
/// </summary>
[CollectionDefinition(nameof(OutputSettingsCollection), DisableParallelization = true)]
public sealed class OutputSettingsCollection;

/// <summary>
/// Story 1.10's verify: <c>serve</c> publishes the probe's <c>videoOptions</c>, which equal what
/// <c>ffmpeg.exe</c> accepts on this PC; the <c>video</c> key refuses 144 fps; and a session encodes
/// the snapshot's codec at its resolution and fps. Every file must play in VLC (headless), when it
/// is installed. The capture windows are borderless and exactly the size asked for; the files are
/// deleted afterwards.
/// </summary>
[Collection(nameof(OutputSettingsCollection))]
public sealed class OutputSettingsTests : IDisposable
{
    // A borderless window that repaints as fast as it can: a new colour every pass.
    private const string WindowScript =
        """
        param([string] $Title, [int] $Width, [int] $Height)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.FormBorderStyle = 'None'
        $form.StartPosition = 'Manual'
        $form.Left = 0
        $form.Top = 0
        $form.Width = $Width
        $form.Height = $Height
        # The hidden launch's SW_HIDE applies to the first window shown: spend it on a throwaway show.
        $form.Show()
        $form.Hide()
        $form.Show()
        $i = 0
        while ($form.Visible) {
            $i = ($i + 1) % 255
            $form.BackColor = [Drawing.Color]::FromArgb($i, 255 - $i, 128)
            $form.Refresh()
            [System.Windows.Forms.Application]::DoEvents()
        }
        """;

    private const string VlcPath = @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-output-").FullName;
    private readonly string _instance = ServeCommand.TestInstancePrefix + Guid.NewGuid().ToString("N");
    private readonly List<Process> _processes = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<VideoCodec> Codecs => [VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1];

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

        foreach (var folder in new[]
        {
            _directory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _instance),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _instance),
        })
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ServeOffersWhatFFmpegAcceptsAndTheVideoKeyRefuses144Fps()
    {
        RequireFFmpeg();
        var expected = ExpectedOffers();

        await StartServeAsync();
        await using var client = await EngineClient.ConnectAsync(PipeName.ForCurrentUser(_instance), cancellationToken: Token);
        var deadline = Stopwatch.StartNew();
        while (!client.State.VideoOptions.Probed && deadline.Elapsed < Timeout)
        {
            await Task.Delay(100, Token);
        }

        var options = client.State.VideoOptions;
        Assert.True(options.Probed, "capture.videoOptions never arrived");
        Assert.Equal(expected, options.Codecs);
        Assert.Equal(VideoOptions.OfferedResolutions, options.Resolutions);
        Assert.Equal(120, options.MaxFps);

        var at144 = Keys.Video.Default with { Fps = 144 };
        var refused = await Assert.ThrowsAsync<ErrorCodeException>(() => client.SendAsync(
            SettingsMessages.Update,
            new SettingsUpdate(0, [SettingsOp.SetGlobal(Keys.Video.Name, Keys.Video.ToJson(at144))]),
            Token));
        Assert.Equal(ErrorCodes.SettingInvalidValue, refused.Code);
        Assert.Equal(0, client.State.Settings.Revision);

        // 120 is the ceiling, and is accepted.
        var at120 = Keys.Video.Default with { Fps = 120 };
        var accepted = await client.SendAsync(
            SettingsMessages.Update,
            new SettingsUpdate(0, [SettingsOp.SetGlobal(Keys.Video.Name, Keys.Video.ToJson(at120))]),
            Token);
        Assert.Equal(1, accepted.Revision);
    }

    [Theory]
    [MemberData(nameof(Codecs))]
    public async Task EachOfferedCodecCapturesA3440x1440WindowAt120Fps(VideoCodec codec)
    {
        RequireFFmpeg();
        var encoders = new VideoEncoderCatalog(NullLoggerFactory.Instance);
        Assert.SkipWhen(encoders.EncoderFor(codec) is null, $"{codec} is not offered on this PC");

        var window = OpenWindow(3440, 1440);
        var output = Path.Combine(_directory, $"{codec}.mp4");
        var video = new VideoSettings(codec, 3440, 1440, 120, 60_000, RateControl.Cbr);
        var outcome = await CaptureAsync(window, video, output, new CaptureSessionOptions { OutputFolder = _directory, OutputPath = output }, encoders, NullLoggerFactory.Instance);
        Assert.Null(outcome.Failure);

        var stream = VideoStream(output);
        Assert.Equal((CodecName(codec), 3440, 1440), (stream.Codec, stream.Width, stream.Height));
        if (codec == VideoCodec.Hevc)
        {
            Assert.Equal("hvc1", stream.Tag);
        }

        // Past the first second, which holds the encoders opening.
        var pts = VideoPts(output).Where(t => t >= 1).ToList();
        var fps = (pts.Count - 1) / (pts[^1] - pts[0]);
        TestContext.Current.TestOutputHelper?.WriteLine($"{codec}: {fps:0.0} fps over {pts.Count} frames");
        Assert.InRange(fps, 90, 125);

        AssertPlaysInVlc(output);
    }

    [Fact]
    public async Task AForcedLibx264SessionWritesAPlayableH264File()
    {
        RequireFFmpeg();
        var window = OpenWindow(1280, 720);
        var output = Path.Combine(_directory, "x264.mp4");
        var video = new VideoSettings(VideoCodec.Hevc, 1280, 720, 60, 8_000, RateControl.Cbr);
        var options = new CaptureSessionOptions { OutputFolder = _directory, OutputPath = output, Encoder = EncoderProbe.SoftwareH264 };

        // The forced encoder wins over the snapshot's codec, and needs no probe.
        var outcome = await CaptureAsync(window, video, output, options, new NoEncoders(), NullLoggerFactory.Instance);
        Assert.Null(outcome.Failure);

        var stream = VideoStream(output);
        Assert.Equal(("h264", 1280, 720), (stream.Codec, stream.Width, stream.Height));
        Assert.True(VideoPts(output).Count > 60, "fewer than 60 frames");

        AssertPlaysInVlc(output);
    }

    [Fact]
    public async Task ACodecThisPCCantEncodeFallsBackToH264WithAWarning()
    {
        RequireFFmpeg();
        var window = OpenWindow(640, 480);
        var output = Path.Combine(_directory, "fallback.mp4");
        var video = new VideoSettings(VideoCodec.Av1, 640, 480, 60, 8_000, RateControl.Vbr);
        var logs = new ListLoggerProvider();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));

        var encoders = new WithoutCodec(new VideoEncoderCatalog(NullLoggerFactory.Instance), VideoCodec.Av1);
        var outcome = await CaptureAsync(window, video, output, new CaptureSessionOptions { OutputFolder = _directory, OutputPath = output }, encoders, loggers);
        Assert.Null(outcome.Failure);

        var stream = VideoStream(output);
        Assert.Equal(("h264", 640, 480), (stream.Codec, stream.Width, stream.Height));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("falls back to H.264", StringComparison.Ordinal));
    }

    private static string CodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "h264",
        VideoCodec.Hevc => "hevc",
        _ => "av1",
    };

    /// <summary>The offers the probe must make: per codec, the first candidate the oracle accepts, and libx264 for H.264 failing that.</summary>
    private static List<CodecOffer> ExpectedOffers()
    {
        var accepted = EncoderOracle.Accepted(EngineBinaries.Folder);
        var offers = new List<CodecOffer>();
        foreach (var codec in EncoderProbe.Codecs)
        {
            var encoder = EncoderProbe.Candidates(codec).FirstOrDefault(accepted.Contains)
                ?? (codec == VideoCodec.H264 ? EncoderProbe.SoftwareH264 : null);
            if (encoder is not null)
            {
                offers.Add(new CodecOffer(codec, encoder, codec != VideoCodec.H264));
            }
        }

        return offers;
    }

    private static void RequireFFmpeg() => Assert.SkipUnless(
        File.Exists(Path.Combine(EngineBinaries.Folder, "ffprobe.exe")) && File.Exists(Path.Combine(EngineBinaries.Folder, "ffmpeg.exe")),
        "native/ffmpeg is not fetched");

    /// <summary>Runs one in-process session on <paramref name="window"/> for 4 s from the anchor, through the orchestrator.</summary>
    private static async Task<SessionOutcome> CaptureAsync(
        WindowInfo window, VideoSettings video, string output, CaptureSessionOptions options, IVideoEncoderCatalog encoders, ILoggerFactory loggers)
    {
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        options = options with { Anchored = qpc => anchored.TrySetResult(qpc) };
        var document = SettingsDocument.Empty with { Global = SettingsDocument.Empty.Global.SetItem(Keys.Video.Name, Keys.Video.ToJson(video)) };
        using var orchestrator = new SessionOrchestrator(new CaptureSessionFactory(options, loggers, encoders), () => document, _ => { });

        var running = await orchestrator.StartAsync(SessionKind.Game, new CaptureTarget((int)window.ProcessId, window.Handle, null), DefaultAudio())
            .WaitAsync(Timeout, Token);
        Assert.Equal(video, running.Spec.Snapshot.Video);
        await anchored.Task.WaitAsync(Timeout, Token);
        await Task.Delay(TimeSpan.FromSeconds(4), Token);
        var outcome = await orchestrator.EndAsync("test over").WaitAsync(Timeout, Token);
        Assert.Equal(output, outcome.Path);
        return outcome;
    }

    private static SessionAudio DefaultAudio()
    {
        var output = AudioEndpoints.Default(PlatformFlow.Output);
        var input = AudioEndpoints.Default(PlatformFlow.Input);
        return new SessionAudio(
        [
            new SessionAudioDevice(output.Id, SettingsFlow.Output, output.FriendlyName),
            new SessionAudioDevice(input.Id, SettingsFlow.Input, input.FriendlyName),
        ]);
    }

    /// <summary>Plays the file headless; it passes when VLC picked a video decoder and logged no decoder error.</summary>
    private void AssertPlaysInVlc(string file)
    {
        Assert.SkipUnless(File.Exists(VlcPath), "VLC is not installed");
        var log = Path.Combine(_directory, Path.GetFileNameWithoutExtension(file) + ".vlc.log");
        var start = new ProcessStartInfo(VlcPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[]
        {
            "-I", "dummy", "--vout=dummy", "--aout=dummy", "--no-video-title-show", "--play-and-exit", "--stop-time=2",
            "-vv", "--file-logging", "--logfile=" + log, file,
        })
        {
            start.ArgumentList.Add(arg);
        }

        using (var vlc = Process.Start(start)!)
        {
            _processes.Add(vlc);
            Assert.True(vlc.WaitForExit(TimeSpan.FromSeconds(60)), "VLC did not exit");
        }

        var lines = File.ReadAllLines(log);
        Assert.Contains(lines, l => l.Contains("using video decoder module", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("decoder error", StringComparison.OrdinalIgnoreCase)
            || l.Contains("no suitable decoder", StringComparison.OrdinalIgnoreCase));
    }

    private WindowInfo OpenWindow(int width, int height)
    {
        var title = "RB-Output-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(_directory, title + ".ps1");
        File.WriteAllText(path, WindowScript);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-File", path, "-Title", title, "-Width", Text(width), "-Height", Text(height) })
        {
            start.ArgumentList.Add(arg);
        }

        _processes.Add(Process.Start(start)!);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (WindowFinder.List().FirstOrDefault(w => w.Title == title) is { } window)
            {
                return window;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException("The test window never appeared.");
    }

    private async Task StartServeAsync()
    {
        var start = new ProcessStartInfo(Path.Combine(EngineBinaries.Folder, "ReplayBuffer.Engine.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "serve", "--instance", _instance })
        {
            start.ArgumentList.Add(arg);
        }

        var process = Process.Start(start)!;
        _processes.Add(process);
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        string? line;
        do
        {
            line = await process.StandardOutput.ReadLineAsync(Token).AsTask().WaitAsync(Timeout, Token);
        }
        while (line is not null && !line.StartsWith("listening ", StringComparison.Ordinal));

        Assert.Equal("listening " + PipeName.ForCurrentUser(_instance), line);
        _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
    }

    private static (string Codec, int Width, int Height, string Tag) VideoStream(string file)
    {
        var stream = Probe(file, "-select_streams", "v:0", "-show_streams").GetProperty("streams")[0];
        return (
            stream.GetProperty("codec_name").GetString()!,
            stream.GetProperty("width").GetInt32(),
            stream.GetProperty("height").GetInt32(),
            stream.GetProperty("codec_tag_string").GetString()!);
    }

    private static List<double> VideoPts(string file) =>
        [.. Probe(file, "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-show_packets")
            .GetProperty("packets").EnumerateArray()
            .Select(p => double.Parse(p.GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture))
            .Order()];

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

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A catalog with no encoder at all: a session must not consult it.</summary>
    private sealed class NoEncoders : IVideoEncoderCatalog
    {
        public string? EncoderFor(VideoCodec codec) => throw new InvalidOperationException("The catalog was consulted.");
    }

    /// <summary>The real catalog, except that <paramref name="missing"/> has no working encoder.</summary>
    private sealed class WithoutCodec(IVideoEncoderCatalog inner, VideoCodec missing) : IVideoEncoderCatalog
    {
        public string? EncoderFor(VideoCodec codec) => codec == missing ? null : inner.EncoderFor(codec);
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new ListLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class ListLogger(ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
