using System.Globalization;
using System.Text;
using Xunit.Sdk;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>The retry rule of the real-time timing tests, without a GPU.</summary>
public sealed class RealTimeAttemptsTests
{
    [Fact]
    public void AnAttemptThatMeetsItsBoundsLaterPassesAndTheEarlierFailureIsLogged()
    {
        var output = new RecordingOutput();
        var calls = 0;

        RealTimeAttempts.Run(attempt =>
        {
            calls++;
            RealTimeAttempts.Bound(attempt >= 2, $"latency p50 {20 - attempt} ms");
        }, output);

        Assert.Equal(2, calls);
        var line = Assert.Single(output.Lines);
        Assert.StartsWith("Attempt 1 of 3 missed a timing bound:", line, StringComparison.Ordinal);
        Assert.Contains("latency p50 19 ms", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoundMissedOnEveryAttemptFailsWithTheLastAttemptsMessage()
    {
        var output = new RecordingOutput();
        var calls = 0;

        var failure = Assert.Throws<RealTimeBoundException>(() => RealTimeAttempts.Run(attempt =>
        {
            calls++;
            RealTimeAttempts.Bound(attempt > RealTimeAttempts.MaxAttempts, $"latency p50 {20 - attempt} ms");
        }, output));

        Assert.Equal(RealTimeAttempts.MaxAttempts, calls);
        Assert.Contains("latency p50 17 ms", failure.Message, StringComparison.Ordinal);
        Assert.Equal(RealTimeAttempts.MaxAttempts, output.Lines.Count);
        Assert.Contains("latency p50 19 ms", output.Lines[0], StringComparison.Ordinal);
        Assert.Contains("latency p50 18 ms", output.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AMissedRangeNamesTheValueAndIsRetried()
    {
        var output = new RecordingOutput();

        RealTimeAttempts.Run(attempt => RealTimeAttempts.InRange(attempt == 1 ? 27L : 30L, 28L, 32L, "fresh slots"), output);

        var line = Assert.Single(output.Lines);
        Assert.Contains("fresh slots 27 is outside [28, 32]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ACorrectnessAssertionIsNotRetried()
    {
        var output = new RecordingOutput();
        var calls = 0;

        // A crash, a pacer fault or a broken PTS is not contention: one bad run in three must fail.
        Assert.Throws<EqualException>(() => RealTimeAttempts.Run(attempt =>
        {
            calls++;
            Assert.Equal(0, attempt == 1 ? -1 : 0);
        }, output));

        Assert.Equal(1, calls);
        Assert.Empty(output.Lines);
    }

    [Fact]
    public void ASkipIsASkipAfterOneCall()
    {
        var output = new RecordingOutput();
        var calls = 0;

        // SkipException is an IAssertionException too; it must not be retried into a failure. Caught by
        // hand: Assert.Throws lets a skip through, so this test would be skipped rather than checked.
        SkipException? skip = null;
        try
        {
            RealTimeAttempts.Run(_ =>
            {
                calls++;
                Assert.Skip("native/ffmpeg is not fetched");
            }, output);
        }
        catch (SkipException e)
        {
            skip = e;
        }

        Assert.NotNull(skip);
        Assert.Contains("native/ffmpeg is not fetched", skip.Message, StringComparison.Ordinal);
        Assert.Equal(1, calls);
        Assert.Empty(output.Lines);
    }

    [Fact]
    public void AnErrorThatIsNotAnAssertionIsNotRetried()
    {
        var calls = 0;

        Assert.Throws<InvalidOperationException>(() => RealTimeAttempts.Run(_ =>
        {
            calls++;
            throw new InvalidOperationException("The test window never appeared.");
        }, new RecordingOutput()));

        Assert.Equal(1, calls);
    }

    private sealed class RecordingOutput : ITestOutputHelper
    {
        private readonly StringBuilder _text = new();

        public List<string> Lines { get; } = [];

        public string Output => _text.ToString();

        public void Write(string message) => _text.Append(message);

        public void Write(string format, params object[] args) => _text.AppendFormat(CultureInfo.InvariantCulture, format, args);

        public void WriteLine(string message)
        {
            Lines.Add(message);
            _text.AppendLine(message);
        }

        public void WriteLine(string format, params object[] args) =>
            WriteLine(string.Format(CultureInfo.InvariantCulture, format, args));
    }
}
