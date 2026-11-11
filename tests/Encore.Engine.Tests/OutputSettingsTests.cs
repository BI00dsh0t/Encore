using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Sessions;
using Encore.Engine.Sessions;
using Encore.Platform.FFmpeg;
using Encore.Platform.FFmpeg.Tests;
using Encore.Platform.Windows;

namespace Encore.Engine.Tests;

/// <summary>Runs the output settings tests alone: the frame rate they measure would suffer from other encode tests.</summary>
[CollectionDefinition(nameof(OutputSettingsCollection), DisableParallelization = true)]
public sealed class OutputSettingsCollection;

/// <summary>The probe's video options match what ffmpeg accepts here, and a session encodes the snapshot's codec and size.</summary>
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

    [Theory(Explicit = true)]
    [Trait("Category", "Heavy")]
    [MemberData(nameof(Codecs))]
    public async Task EachOfferedCodecCapturesA3440x1440WindowAt120Fps(VideoCodec codec)
    {
        SessionTestKit.RequireCapture();
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

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AForcedLibx264SessionWritesAPlayableH264File()
    {
        SessionTestKit.RequireCapture();
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
        SessionTestKit.RequireCapture();
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

    /// <summary>The offers the probe must make: per codec, the first candidate the oracle accepts, else libx264 for H.264.</summary>
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

    private WindowInfo OpenWindow(int width, int height) =>
        SessionTestKit.OpenWindow(_directory, _processes, WindowScript, "-Width", Text(width), "-Height", Text(height));

    private void AssertPlaysInVlc(string file) => SessionTestKit.AssertPlaysInVlc(file, _directory, _processes);

    private static void RequireFFmpeg() => SessionTestKit.RequireFFmpeg();

    private static Task<SessionOutcome> CaptureAsync(
        WindowInfo window, VideoSettings video, string output, CaptureSessionOptions options, IVideoEncoderCatalog encoders, ILoggerFactory loggers) =>
        SessionTestKit.CaptureAsync(window, video, output, options, encoders, loggers, TimeSpan.FromSeconds(4));

    private async Task StartServeAsync()
    {
        var start = new ProcessStartInfo(Path.Combine(EngineBinaries.Folder, "Encore.Engine.exe"))
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

    private static (string Codec, int Width, int Height, string Tag) VideoStream(string file) => SessionTestKit.VideoStream(file);

    private static List<double> VideoPts(string file) => SessionTestKit.VideoPts(file);

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
