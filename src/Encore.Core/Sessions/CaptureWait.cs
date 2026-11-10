using Encore.Contracts;

namespace Encore.Core.Sessions;

/// <summary>How the game session follows the Target's process, not one window. Orchestrator thread; docs/code-notes.md.</summary>
public sealed record CaptureWait(
    Func<TargetCandidate, CaptureWindow> Find,
    Action<TargetCandidate, string> Waiting,
    Action<TargetCandidate, bool> Ended,
    Func<Exception, bool>? Retries = null)
{
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan DefaultFirstRetry = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan DefaultMaxRetry = TimeSpan.FromSeconds(30);

    /// <summary>How often the Target's windows are checked while none can be captured, and the captured one after that.</summary>
    public TimeSpan Poll { get; init; } = DefaultPoll;

    /// <summary>The wait before the first retry of a failed session or retarget.</summary>
    public TimeSpan FirstRetry { get; init; } = DefaultFirstRetry;

    /// <summary>The longest wait between retries.</summary>
    public TimeSpan MaxRetry { get; init; } = DefaultMaxRetry;

    /// <summary>Whether <paramref name="failure"/> of a session on the Target is retried.</summary>
    public bool Retry(Exception failure) =>
        Retries?.Invoke(failure) ?? failure is ErrorCodeException { Code: ErrorCodes.CaptureFailed };

    /// <summary>The wait before retry <paramref name="failures"/> (1 for the first) of a run.</summary>
    public TimeSpan Backoff(int failures)
    {
        var wait = FirstRetry;
        for (var i = 1; i < failures && wait < MaxRetry; i++)
        {
            wait += wait;
        }

        return wait < MaxRetry ? wait : MaxRetry;
    }
}
