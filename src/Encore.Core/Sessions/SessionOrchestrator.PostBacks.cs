namespace Encore.Core.Sessions;

public sealed partial class SessionOrchestrator
{
    private sealed record SessionClosed(long SessionId, string Reason) : SessionMessage;

    private sealed record SessionFailed(long SessionId, Exception Failure) : SessionMessage;

    /// <summary>Session <paramref name="SessionId"/>'s output anchored at its first frame, at <paramref name="Qpc"/>.</summary>
    private sealed record SessionAnchored(long SessionId, long Qpc) : SessionMessage;

    /// <summary>A session's encoder chain was forcibly rebuilt; its frames from Qpc on are the new chain's.</summary>
    private sealed record SessionChainRestarted(long SessionId, long Qpc) : SessionMessage;

    /// <summary>Session <paramref name="SessionId"/>'s GPU device was lost, or an attempt to recover it failed.</summary>
    private sealed record SessionDeviceLost(long SessionId, Exception Failure) : SessionMessage;

    /// <summary>A session's recovery retry timer fired; ignored once that session is gone or recovered.</summary>
    private sealed record RetryDeviceRecovery(long SessionId) : SessionMessage;

    /// <summary>The ring recheck timer fired; ignored once no Rewind waits for room.</summary>
    private sealed record RingRecheck : SessionMessage;

    /// <summary>The capture wait's timer fired; ignored once nothing waits for the Target's window.</summary>
    private sealed record CaptureRetry : SessionMessage;

    /// <summary>The window watch of session <paramref name="SessionId"/> fired; ignored once that session is gone.</summary>
    private sealed record FollowCheck(long SessionId) : SessionMessage;

    /// <summary>The drain timer of session <paramref name="SessionId"/> fired; ignored once that drain is gone.</summary>
    private sealed record DrainExpired(long SessionId) : SessionMessage;

    /// <summary>A Recording writer's result, posted from its thread.</summary>
    private sealed record RecordingWritten(RecordingRun Recording, Exception? Error) : SessionMessage;

    /// <summary>A Clip writer's result, posted from its thread, with the session, press and write it came from.</summary>
    private sealed record ClipWritten(long SessionId, long Qpc, IClipWrite Write, string Path, Exception? Error) : SessionMessage;
}
