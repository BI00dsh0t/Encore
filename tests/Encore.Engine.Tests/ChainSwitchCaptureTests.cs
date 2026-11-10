using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Audio;
using Encore.Core.Output;
using Encore.Core.Sessions;
using Encore.Engine.Sessions;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Engine.Tests;

/// <summary>
/// Profile settings at the next Reset (story 4.9, R7, R8), in process: the Global layout has Tracks
/// Game and Mic (the Mic Device <c>mic</c>, <c>muteAffected</c>) at 640×480. About 3 s after the
/// anchor the game's profile overrides the video to 1280×720 and the layout to three Tracks: Game;
/// Voice (<c>mic</c>, no Mic Device here, both <c>muteAffected</c>) and Mic2 (its own mic <c>mic2</c>).
/// The mic is muted throughout. The save 1.5 s later still has the old Tracks and size; the save
/// after it has the new ones, from where the first ended.
/// </summary>
public sealed partial class ChainSwitchCaptureTests : IDisposable
{
    private const string WindowScript =
        """
        param([string] $Title)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.Width = 641
        $form.Height = 481
        $start = [Diagnostics.Stopwatch]::StartNew()
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 16
        $timer.Add_Tick({
            $v = [int](($start.ElapsedMilliseconds / 4) % 255)
            $form.BackColor = [Drawing.Color]::FromArgb($v, 255 - $v, 128)
        })
        $timer.Start()
        $form.Show()
        $form.Hide()
        [void] $form.ShowDialog()
        """;

    private const string ProfileId = @"c:\games\switch game\switchgame.exe";
    private const double MicAmplitude = 0.2;
    private const int Fps = 30;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>−60 dBFS, as an RMS.</summary>
    private static readonly double Silence = Math.Pow(10, -60 / 20.0);

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-chainswitch-").FullName;
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
    public async Task AProfilesNewLayoutAndVideoApplyFromTheClipAfterTheNextSaveWithNoGap()
    {
        SessionTestKit.RequireFFmpeg();
        using var hub = new ToneHub(new Dictionary<string, (double, double)>
        {
            ["game"] = (0.1, 440),
            ["mic"] = (MicAmplitude, 1000),
            ["mic2"] = (MicAmplitude, 660),
        });
        var game = new LayoutDevice(Guid.NewGuid(), "game", "Game device", "", SettingsFlow.Output, IsMic: false);
        var mic = new LayoutDevice(Guid.NewGuid(), "mic", "Mic device", "", SettingsFlow.Input, IsMic: true);
        var voice = mic with { Key = Guid.NewGuid(), IsMic = false };
        var mic2 = new LayoutDevice(Guid.NewGuid(), "mic2", "Own mic", "", SettingsFlow.Input, IsMic: true);
        var globalLayout = new TrackLayout(
            [game, mic],
            [new Track(Guid.NewGuid(), "Game", [game.Key], MuteAffected: false), new Track(Guid.NewGuid(), "Mic", [mic.Key], MuteAffected: true)]);
        var profileLayout = new TrackLayout(
            [game, voice, mic2],
            [
                new Track(Guid.NewGuid(), "Game", [game.Key], MuteAffected: false),
                // muteAffected, so only the profile's own isMic decides: the Global mic is no Mic Device here and stays audible.
                new Track(Guid.NewGuid(), "Voice", [voice.Key], MuteAffected: true),
                new Track(Guid.NewGuid(), "Mic2", [mic2.Key], MuteAffected: true),
            ]);
        var start = SettingsDocument.Empty with
        {
            Global = SettingsDocument.Empty.Global
                .SetItem(Keys.Video.Name, Keys.Video.ToJson(new VideoSettings(VideoCodec.H264, 640, 480, Fps, 4_000, RateControl.Cbr)))
                .SetItem(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(globalLayout))
                .SetItem(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(_directory))
                .SetItem(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(0)),
            Profiles = SettingsMaps.EmptyProfiles.Add(ProfileId, new GameProfile { Name = "Switch Game" }),
        };
        var changed = start with
        {
            Profiles = SettingsMaps.EmptyProfiles.Add(ProfileId, new GameProfile
            {
                Name = "Switch Game",
                Overrides = SettingsMaps.EmptyValues
                    .Add(Keys.Video.Name, Keys.Video.ToJson(new VideoSettings(VideoCodec.H264, 1280, 720, Fps, 6_000, RateControl.Cbr)))
                    .Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(profileLayout)),
            }),
        };
        var document = start;

        var window = SessionTestKit.OpenWindow(_directory, _processes, WindowScript);
        var logs = new ListLoggerProvider();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();
        var gaps = new ConcurrentQueue<string>();
        var options = new CaptureSessionOptions
        {
            OutputFolder = _directory,
            Replay = true,
            Anchored = qpc => anchored.TrySetResult(qpc),
            ResetGap = gaps.Enqueue,
        };

        ClipSaved first, second;
        using (var orchestrator = new SessionOrchestrator(
            new CaptureSessionFactory(options, loggers, hub),
            () => Volatile.Read(ref document),
            _ => { },
            targetCapture: new TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), (_, e) => saved.Writer.TryComplete(e)),
            clips: new ReplayClips(OutputPathAllocator.System, () => _directory, c => saved.Writer.TryWrite(c), e => saved.Writer.TryComplete(e))))
        {
            orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "switchgame.exe"), TargetChangeReason.Detected));
            await anchored.Task.WaitAsync(Timeout, Token);
            await Task.Delay(TimeSpan.FromSeconds(3), Token);

            Volatile.Write(ref document, changed);
            Assert.True(orchestrator.Post(new SettingsUpdated()));
            await Task.Delay(TimeSpan.FromSeconds(1.5), Token);

            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
            first = await saved.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);
            await Task.Delay(TimeSpan.FromSeconds(3), Token);

            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
            second = await saved.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);
        }

        // The first Clip: the old Tracks and size.
        Assert.Equal(["Game", "Mic"], AudioTitles(first.Path));
        var (_, width, height, _) = SessionTestKit.VideoStream(first.Path);
        Assert.Equal((640, 480), (width, height));

        // The second: the profile's Tracks and size; its own mic is muted, the Global one is not a Mic Device here.
        Assert.Equal(["Game", "Voice", "Mic2"], AudioTitles(second.Path));
        (_, width, height, _) = SessionTestKit.VideoStream(second.Path);
        Assert.Equal((1280, 720), (width, height));
        SessionTestKit.AssertDecodesCleanly(second.Path);
        var micRms = MicAmplitude / Math.Sqrt(2);
        var voiceRms = Rms(Decode(second.Path, 1));
        Assert.True(voiceRms >= micRms * 0.9 && voiceRms <= micRms * 1.1, $"Voice: RMS {voiceRms}, expected {micRms}");
        Assert.True(Rms(Decode(second.Path, 2)) < Silence, $"Mic2 wasn't silent: RMS {Rms(Decode(second.Path, 2))}");

        // The second starts within one frame of the first's end, and no gap was raised.
        var spans = logs.Entries.Select(e => ClipLine().Match(e.Message)).Where(m => m.Success)
            .Select(m => (Start: Seconds(m.Groups["start"].Value), End: Seconds(m.Groups["end"].Value)))
            .ToList();
        Assert.Equal(2, spans.Count);
        Assert.InRange(spans[1].Start, spans[0].End - (1.0 / Fps) - 0.001, spans[0].End + (1.0 / Fps) + 0.001);
        Assert.Empty(gaps);
    }

    private static double Seconds(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    private static List<string?> AudioTitles(string file) =>
        [.. SessionTestKit.Probe(file, "-select_streams", "a", "-show_streams").GetProperty("streams").EnumerateArray()
            .Select(s => s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("name", out var title) ? title.GetString() : null)];

    /// <summary>The RMS of <paramref name="samples"/> away from the Clip's first and last 0.5 s.</summary>
    private static double Rms(float[] samples)
    {
        var from = AudioFormat.SampleRate / 2;
        var to = samples.Length - (AudioFormat.SampleRate / 2);
        Assert.True(to - from > AudioFormat.SampleRate, "the Clip is shorter than 2 s");
        double sum = 0;
        for (var i = from; i < to; i++)
        {
            sum += samples[i] * samples[i];
        }

        return Math.Sqrt(sum / (to - from));
    }

    /// <summary>Audio stream <paramref name="index"/>'s left channel, decoded whole.</summary>
    private float[] Decode(string file, int index)
    {
        var raw = Path.Combine(_directory, $"{Path.GetFileNameWithoutExtension(file)}-a{index}.f32");
        var start = new ProcessStartInfo(Path.Combine(EngineBinaries.Folder, "ffmpeg.exe"))
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "error", "-y", "-i", file, "-map", $"0:a:{index}", "-f", "f32le", raw })
        {
            start.ArgumentList.Add(arg);
        }

        using (var ffmpeg = Process.Start(start)!)
        {
            var stderr = ffmpeg.StandardError.ReadToEnd();
            ffmpeg.WaitForExit();
            Assert.True(ffmpeg.ExitCode == 0, stderr);
        }

        var samples = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(raw));
        var left = new float[samples.Length / AudioFormat.Channels];
        for (var i = 0; i < left.Length; i++)
        {
            left[i] = samples[i * AudioFormat.Channels];
        }

        return left;
    }

    [GeneratedRegex(@"^Clip of [\d.]+ s, (?<start>[\d.]+) s to (?<end>[\d.]+) s after the anchor")]
    private static partial Regex ClipLine();

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
