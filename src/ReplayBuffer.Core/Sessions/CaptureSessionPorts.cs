namespace ReplayBuffer.Core.Sessions;

/// <summary>Starts capture sessions. Called only on the orchestrator thread.</summary>
public interface ICaptureSessionFactory
{
    /// <summary>
    /// Starts a session for <paramref name="spec"/>. The session reports back only through
    /// <paramref name="events"/>, which never blocks; a throw means nothing was started.
    /// </summary>
    ICaptureSession Start(SessionSpec spec, ISessionEvents events);
}

/// <summary>A running capture session. Every call comes from the orchestrator thread.</summary>
public interface ICaptureSession
{
    /// <summary>
    /// Moves capture to another window of the same process, keeping the encoders and the file,
    /// so the output keeps one unbroken set of streams. A throw leaves the session without capture;
    /// the orchestrator then ends it.
    /// </summary>
    void Retarget(CaptureTarget target);

    /// <summary>
    /// Sets mic mute from <paramref name="qpc"/> on: each Mic Device in each <c>muteAffected</c>
    /// Track is silenced (or heard again) from the first audio frame at or after it, before the mix.
    /// Frames already mixed are never changed. Called on the orchestrator thread; never blocks on
    /// the audio threads.
    /// </summary>
    void SetMicMuted(bool muted, long qpc);

    /// <summary>
    /// Stops capture, finishes the output and frees everything. <paramref name="reason"/> is why,
    /// unless the session had already stopped itself (closed or failed), whose reason wins.
    /// </summary>
    SessionOutcome End(string reason);
}

/// <summary>
/// Where the orchestrator keeps mic mute between runs (AD-5: runtime state, never a setting).
/// Neither call throws.
/// </summary>
public interface IMicMuteStore
{
    /// <summary>The saved mic mute; true (muted) when nothing is saved or the saved state can't be read.</summary>
    bool Load();

    /// <summary>Saves <paramref name="muted"/>; a failure is logged at Warning and the call returns.</summary>
    void Save(bool muted);
}

/// <summary>
/// How a session posts back to the orchestrator from its media threads (AD-19). Both calls only
/// post a message and return; the orchestrator ends the session.
/// </summary>
public interface ISessionEvents
{
    /// <summary>The captured window closed.</summary>
    void Closed(string reason);

    /// <summary>Capture or encoding failed.</summary>
    void Failed(Exception failure);
}

/// <summary>How a session ended: the finished file (null when none was written), why, and any failure.</summary>
public sealed record SessionOutcome(string? Path, string Reason, Exception? Failure);
