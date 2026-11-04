namespace ReplayBuffer.Bench;

/// <summary>
/// <c>bench soak</c>'s timetable: a sample at the start and every <c>sampleEvery</c>, a press every
/// <c>pressEvery</c> from the start, until <c>duration</c> has passed, then a last sample. A sample or press
/// that runs late moves its next one to the following slot rather than bunching up. The clock and the wait
/// are injected, so a test can run an hour's schedule in no time.
/// </summary>
internal sealed class SoakSchedule(TimeSpan duration, TimeSpan sampleEvery, TimeSpan pressEvery, TimeProvider time, Action<TimeSpan, CancellationToken>? sleep = null)
{
    private readonly Action<TimeSpan, CancellationToken> _sleep = sleep ?? ((span, token) => token.WaitHandle.WaitOne(span));

    public int Samples { get; private set; }

    public int Presses { get; private set; }

    /// <summary>Runs the schedule; returns why it stopped early (<paramref name="stop"/>'s reason), or null when it ran to the end.</summary>
    public string? Run(Action sample, Action press, Func<string?> stop, CancellationToken cancellation)
    {
        var start = time.GetUtcNow();
        var end = start + duration;
        var nextSample = start;
        var nextPress = start + pressEvery;
        while (true)
        {
            if (stop() is { } reason)
            {
                return reason;
            }

            if (cancellation.IsCancellationRequested)
            {
                return "stopped (Ctrl+C)";
            }

            var now = time.GetUtcNow();
            if (now >= end)
            {
                break;
            }

            if (now >= nextSample)
            {
                sample();
                Samples++;
                while (nextSample <= now)
                {
                    nextSample += sampleEvery;
                }

                continue;
            }

            if (now >= nextPress)
            {
                press();
                Presses++;
                while (nextPress <= now)
                {
                    nextPress += pressEvery;
                }

                continue;
            }

            var next = new[] { nextSample, nextPress, end }.Min();
            _sleep(next - now, cancellation);
        }

        sample();
        Samples++;
        return null;
    }
}
