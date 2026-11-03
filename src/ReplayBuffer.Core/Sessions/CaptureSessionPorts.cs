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

    /// <summary>
    /// Stops capture and finishes the output as <see cref="End"/> does, but a session holding a
    /// Replay Buffer keeps it (R1's exit drain): <see cref="Save"/> still cuts from it, up to the end
    /// of what it holds, until <see cref="Release"/>. A session with no Replay Buffer just ends.
    /// </summary>
    SessionOutcome Drain(string reason) => End(reason);

    /// <summary>
    /// Frees a Replay Buffer kept by <see cref="Drain"/>; a later <see cref="Save"/> makes nothing.
    /// A write in flight keeps its own lease, so its Clip still completes (AD-8). Never blocks.
    /// </summary>
    void Release()
    {
    }

    /// <summary>
    /// The Buffer Window, in seconds, from the next Reset on: a session holding a Replay Buffer
    /// resizes it there when the value changed (AD-8). Called on the orchestrator thread just
    /// before a Reset; never blocks.
    /// </summary>
    void SetBufferWindow(int seconds)
    {
    }

    /// <summary>
    /// Builds <paramref name="next"/>'s encoder chain in advance, idle, for the next save press
    /// (R7, R8): the first press whose write starts once it is built switches the session to it, so
    /// the Clip after that one has its Tracks and video, with no gap. Null drops any chain prepared
    /// and not yet switched to. A session holding no Replay Buffer ignores it; a failed build is
    /// logged and dropped, and the next call retries. Called on the orchestrator thread; never blocks.
    /// </summary>
    void PrepareChain(SessionSpec? next)
    {
    }

    /// <summary>
    /// A Clip of the Replay Buffer up to <paramref name="qpc"/> (the press, AD-6), not yet written;
    /// null when this session holds no Replay Buffer, or nothing yet. The session asks its encoder for
    /// a keyframe on the next frame (AD-10), where the Clip ends. Throws an
    /// <see cref="Contracts.ErrorCodeException"/> with <see cref="Contracts.ErrorCodes.ClipTooShort"/>
    /// when the press is less than 1 s after the session's start. Called on the orchestrator thread.
    /// </summary>
    IClipWrite? Save(long qpc) => null;

    /// <summary>
    /// A Recording from <paramref name="qpc"/> (the press, AD-6) on this session's encoder chain,
    /// not yet started; null when this session can't record now: it holds no Replay Buffer, hasn't
    /// anchored, its output has finished, or a Recording is already attached to it. Called on the
    /// orchestrator thread; never blocks.
    /// </summary>
    IRecordingWrite? Record(long qpc) => null;
}

/// <summary>A Recording a session can write (R2): one fragmented MP4 from the session's encoders.</summary>
public interface IRecordingWrite
{
    /// <summary>
    /// Starts writing to <paramref name="path"/>, which the allocator reserved (AD-21), off the
    /// calling thread, from the first video keyframe after the press. The write runs until
    /// <see cref="Stop"/>, or until the session's output ends; then <paramref name="done"/> is called
    /// once, from the writer's thread, with null or the failure. A failed write whose file holds data
    /// keeps it, finished as far as it got; one that wrote nothing leaves no file behind, the reserved
    /// one included. A throw means nothing was started. Called on the orchestrator thread; never blocks.
    /// </summary>
    void Start(string path, Action<Exception?> done);

    /// <summary>
    /// Ends the Recording at <paramref name="qpc"/> (the press): it keeps what was captured before
    /// it, then finishes its file and calls the <see cref="Start"/> callback. Called on the
    /// orchestrator thread; never blocks on the file.
    /// </summary>
    void Stop(long qpc);
}

/// <summary>A Clip a session can write (AD-8, AD-9).</summary>
public interface IClipWrite
{
    /// <summary>
    /// Writes the Clip to <paramref name="path"/>, which the allocator reserved (AD-21), off the
    /// calling thread; then calls <paramref name="done"/> once, from the writer's thread, with null or
    /// the failure (an <see cref="Contracts.ErrorCodeException"/> with
    /// <see cref="Contracts.ErrorCodes.ClipTooShort"/> when the buffer held nothing to cut). A failed
    /// write leaves no file behind, the reserved one included. Called on the orchestrator thread;
    /// never blocks.
    /// </summary>
    void Write(string path, Action<Exception?> done);

    /// <summary>
    /// The spec this write's press switched the session to (<see cref="ICaptureSession.PrepareChain"/>),
    /// set once <see cref="Write"/> has armed the switch; null for none. The switch stands whatever
    /// the write's outcome: the session's later Clips come from the new chain. Read on the
    /// orchestrator thread, after <see cref="Write"/> returns.
    /// </summary>
    SessionSpec? SwitchesTo => null;

    /// <summary>
    /// Makes this Clip's end the Replay Buffer's Reset (R5): the session's next Clip starts at the
    /// keyframe this one ended on, so the two meet with no gap. The orchestrator calls it, on its
    /// thread, only once this write has succeeded (AD-19).
    /// </summary>
    void Reset()
    {
    }
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
