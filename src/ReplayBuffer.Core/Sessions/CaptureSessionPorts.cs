using ReplayBuffer.Contracts.Messages;

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

    /// <summary>
    /// How many encoder chains the session runs now (AD-7): its current one, one armed or prepared
    /// for a switch, and one being built; a chain already being torn down doesn't count. Called on
    /// the orchestrator thread; never blocks.
    /// </summary>
    int Chains => 1;

    /// <summary>
    /// Switches the session to <paramref name="next"/>'s encoder chain at a Reset, tearing the
    /// current one down before the new one is built, so it never runs two (AD-7, while a Monitor
    /// Recording holds the third): the Replay Buffer starts empty on the new chain, and the gap
    /// between the chains is raised as <see cref="Contracts.ErrorCodes.ResetGap"/>. False when the
    /// session can't switch now (no Replay Buffer, a write or Recording on the chain, or a switch
    /// already under way); the caller retries at the next Reset. Called on the orchestrator thread
    /// just after a Reset; never blocks.
    /// </summary>
    bool SwitchChain(SessionSpec next) => false;

    /// <summary>
    /// Starts one attempt, off the calling thread, to rebuild the session's capture, device and
    /// encoder chains after <see cref="ISessionEvents.DeviceLost"/>: it posts
    /// <see cref="ISessionEvents.ChainRestarted"/> when it succeeds, or
    /// <see cref="ISessionEvents.DeviceLost"/> again when it fails. False when nothing is lost or
    /// an attempt is already running. Called on the orchestrator thread; never blocks.
    /// </summary>
    bool RecoverDevice() => false;

    /// <summary>
    /// The PC is going to sleep (T-02): stops feeding the encoder chains, then tears capture, the
    /// chains and the device down off the calling thread, keeping the Replay Buffer saveable, and
    /// ends a Recording on the session at its last fragment. No frames are captured until
    /// <see cref="RecoverDevice"/> succeeds. False when it can't (not anchored, no Replay Buffer,
    /// stopped). Called on the orchestrator thread; never blocks.
    /// </summary>
    bool Suspend() => false;

    /// <summary>
    /// The GDI device name (such as <c>\\.\DISPLAY2</c>) of the monitor a Monitor session captures,
    /// resolved when it started; null for a game session, or when the monitor has no name. Called on
    /// the orchestrator thread; never blocks.
    /// </summary>
    string? MonitorDevice => null;

    /// <summary>
    /// The GDI device names of every monitor a Monitor session still captures (story 11.3): several
    /// for a composited Recording, else <see cref="MonitorDevice"/> alone. Called on the orchestrator
    /// thread; never blocks.
    /// </summary>
    IReadOnlyList<string> MonitorDevices => MonitorDevice is { } device ? [device] : [];

    /// <summary>
    /// Stops capturing <paramref name="device"/>, one of several composited monitors that was removed
    /// (story 11.3): its area turns black and the Recording goes on. False when the session doesn't
    /// capture it. Called on the orchestrator thread.
    /// </summary>
    bool DropMonitor(string device) => false;

    /// <summary>
    /// Story 12.2 (AD-7, AD-26): whether the session may build a chain ahead beside its current one,
    /// for a move to the game's new size. False while a Monitor Recording holds the other chain, when a
    /// move tears the current chain down first. True by default. Called on the orchestrator thread.
    /// </summary>
    void SetBuildAhead(bool allowed)
    {
    }
}

/// <summary>
/// The attached display monitors and their changes (T-03), AD-19's input: the orchestrator ends a
/// Monitor Recording whose monitor is gone, and the engine publishes the new list.
/// </summary>
public interface IDisplayMonitors
{
    /// <summary>Every attached monitor now, the primary first. Never throws; empty when they can't be read.</summary>
    IReadOnlyList<DisplayMonitor> List();

    /// <summary>
    /// Calls <paramref name="changed"/> after each change of the displays (a monitor connected or
    /// removed, the primary moved, a mode changed), on any thread, until the returned subscription is
    /// disposed. Never throws; a source that can't subscribe does nothing.
    /// </summary>
    IDisposable Subscribe(Action changed);
}

/// <summary>
/// The PC's sleep and resume (T-02), AD-19's input: the orchestrator decides what a sleep stops
/// and what a resume rebuilds.
/// </summary>
public interface IPowerNotifications
{
    /// <summary>
    /// Calls <paramref name="changed"/> on each power change, on any thread, until the returned
    /// subscription is disposed. Never throws; a source that can't subscribe does nothing.
    /// </summary>
    IDisposable Subscribe(Action<PowerChange> changed);
}

/// <summary>A power change <see cref="IPowerNotifications"/> reports.</summary>
public enum PowerChange
{
    /// <summary>The PC is going to sleep.</summary>
    Suspending,

    /// <summary>The PC woke from sleep.</summary>
    Resumed,
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
/// Where the orchestrator keeps the paths of the Recordings being written (FR-12, AD-5: runtime
/// state), so the next start can trim the ones a dead engine left open and say they were cut short.
/// Called on the orchestrator thread; neither call throws.
/// </summary>
public interface IOpenRecordings
{
    /// <summary>A Recording at <paramref name="path"/> is about to be written.</summary>
    void Opened(string path);

    /// <summary>The Recording at <paramref name="path"/> is no longer being written, however it ended.</summary>
    void Closed(string path);
}

/// <summary>
/// How a session posts back to the orchestrator from its media threads (AD-19). Every call only
/// posts a message and returns; the orchestrator ends the session, and starts and stops Recordings.
/// </summary>
public interface ISessionEvents
{
    /// <summary>The captured window closed.</summary>
    void Closed(string reason);

    /// <summary>Capture or encoding failed.</summary>
    void Failed(Exception failure);

    /// <summary>
    /// The session's encoder chain was forcibly rebuilt (R4); frames from <paramref name="qpc"/> on
    /// are the new chain's. A Recording that ran on the old chain has ended at its last packet.
    /// </summary>
    void ChainRestarted(long qpc)
    {
    }

    /// <summary>
    /// The session's output anchored at its first frame, at <paramref name="qpc"/>: from now on
    /// <see cref="ICaptureSession.Record"/> can succeed.
    /// </summary>
    void Anchored(long qpc)
    {
    }

    /// <summary>
    /// The session's GPU device was removed or reset (a driver reset), or an attempt to recover
    /// from that failed. No frames are captured or encoded until a recovery
    /// (<see cref="ICaptureSession.RecoverDevice"/>) succeeds, which posts
    /// <see cref="ChainRestarted"/>.
    /// </summary>
    void DeviceLost(Exception failure)
    {
    }
}

/// <summary>How a session ended: the finished file (null when none was written), why, and any failure.</summary>
public sealed record SessionOutcome(string? Path, string Reason, Exception? Failure);
