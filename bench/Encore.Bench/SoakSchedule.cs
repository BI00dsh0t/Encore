namespace Encore.Bench;

/// <summary><c>bench soak</c>'s timetable of samples, presses and one-shot events; the clock and wait are injected for tests.</summary>
internal sealed class SoakSchedule(
    TimeSpan duration,
    TimeSpan sampleEvery,
    TimeSpan pressEvery,
    TimeProvider time,
    Action<TimeSpan, CancellationToken>? sleep = null,
    IReadOnlyList<(TimeSpan At, Action Run)>? events = null)
{
    private readonly Action<TimeSpan, CancellationToken> _sleep = sleep ?? ((span, token) => token.WaitHandle.WaitOne(span));

    public int Samples { get; private set; }

    public int Presses { get; private set; }

    /// <summary>Runs the schedule; returns why it stopped early, or null when it ran to the end.</summary>
    public string? Run(Action sample, Action press, Func<string?> stop, CancellationToken cancellation)
    {
        var start = time.GetUtcNow();
        var end = start + duration;
        var nextSample = start;
        var nextPress = start + pressEvery;
        // Read at the start of the run, so a caller can fill the list once what the events use exists.
        List<(TimeSpan At, Action Run)> oneShots = [.. (events ?? []).OrderBy(e => e.At)];
        var nextEvent = 0;
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

            if (nextEvent < oneShots.Count && now >= start + oneShots[nextEvent].At)
            {
                oneShots[nextEvent++].Run();
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

            var next = new[] { nextSample, nextPress, end, nextEvent < oneShots.Count ? start + oneShots[nextEvent].At : end }.Min();
            _sleep(next - now, cancellation);
        }

        sample();
        Samples++;
        return null;
    }
}
