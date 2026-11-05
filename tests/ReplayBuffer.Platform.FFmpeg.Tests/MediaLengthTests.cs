using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ReplayBuffer.Platform.FFmpeg.Tests;

/// <summary>The Library's length reader against <c>ffprobe</c>, on files <c>ffmpeg.exe</c> generates.</summary>
public sealed class MediaLengthTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("rb-length-").FullName;
    private readonly ListLogger _log = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AClipReadsAsLongAsFFprobeSays()
    {
        RequireFFmpeg();
        var clip = Generate("clip.mp4", 30, "");

        var length = MediaLength.Read(clip, _log);

        Assert.NotNull(length);
        Assert.InRange(length.Value, ProbeDuration(clip) - 0.1, ProbeDuration(clip) + 0.1);
    }

    [Fact]
    public void AFragmentedMp4CutMidFragmentReadsToItsLastCompleteFragment()
    {
        RequireFFmpeg();
        var full = Generate("frag.mp4", 10, "-movflags frag_keyframe+empty_moov+default_base_moof -frag_duration 1000000");
        var cut = Path.Combine(_directory, "cut.mp4");
        var bytes = File.ReadAllBytes(full);
        File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);

        var length = MediaLength.Read(cut, _log);

        Assert.NotNull(length);
        Assert.InRange(length.Value, ProbeDuration(cut) - 0.1, ProbeDuration(cut) + 0.1);
        Assert.True(length.Value < 10, $"The cut file read as {length.Value} s.");
    }

    [Fact]
    public void TheReadersHandleLetsTheFileBeRenamed()
    {
        // The bug that bit (full-run-flaky-tests): FFmpeg's own file protocol shares no delete, so a
        // rename during a starved length read failed as "in use".
        RequireFFmpeg();
        var clip = Generate("held.mp4", 30, "");
        var renamed = Path.Combine(_directory, "renamed.mp4");

        using (new SharedInput(clip))
        {
            File.Move(clip, renamed);
        }

        Assert.NotNull(MediaLength.Read(renamed, _log));
    }

    [Fact]
    public void AnUnreadableFileHasNoLengthAndIsLogged()
    {
        RequireFFmpeg();
        var junk = Path.Combine(_directory, "junk.mp4");
        var bytes = new byte[64 * 1024];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(junk, bytes);

        Assert.Null(MediaLength.Read(junk, _log));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(junk, StringComparison.Ordinal));
    }

    private static void RequireFFmpeg() =>
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")), "native/ffmpeg is not fetched");

    private string Generate(string name, int seconds, string muxerOptions)
    {
        var path = Path.Combine(_directory, name);
        Run(
            "ffmpeg.exe",
            $"-v error -y -f lavfi -i testsrc=size=320x240:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 -t {seconds} "
            + $"-c:v libx264 -preset ultrafast -c:a aac {muxerOptions} \"{path}\"");
        return path;
    }

    private double ProbeDuration(string path) =>
        double.Parse(Run("ffprobe.exe", $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{path}\""), CultureInfo.InvariantCulture);

    private string Run(string tool, string arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, tool), arguments)
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout.Result.Trim();
    }

    private sealed class ListLogger : ILogger
    {
        private readonly Lock _lock = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_lock)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
