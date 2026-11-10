using Encore.Contracts.Messages;

namespace Encore.Core.Sessions;

/// <summary>Starts capture sessions. Called only on the orchestrator thread.</summary>
public interface ICaptureSessionFactory
{
    /// <summary>Starts a session for a spec; it reports back only through events, and a throw means nothing was started.</summary>
    ICaptureSession Start(SessionSpec spec, ISessionEvents events);
}

/// <summary>A running capture session. Every call comes from the orchestrator thread.</summary>
public interface ICaptureSession
{
    /// <summary>Moves capture to another window of the same process, keeping the encoders and the file.</summary>
    void Retarget(CaptureTarget target);

    /// <summary>Sets mic mute from a QPC time on, before the mix; frames already mixed never change. Orchestrator thread.</summary>
    void SetMicMuted(bool muted, long qpc);

    /// <summary>Stops capture, finishes the output and frees everything; a session that stopped itself keeps its own reason.</summary>
    SessionOutcome End(string reason);

    /// <summary>Like <see cref="End"/>, but a session with a Rewind keeps it for saves until <see cref="Release"/>.</summary>
    SessionOutcome Drain(string reason) => End(reason);

    /// <summary>Frees a Rewind kept by <see cref="Drain"/>; a write in flight keeps its own lease. Never blocks.</summary>
    void Release()
    {
    }

    /// <summary>The Buffer Window, in seconds, from the next Reset on. Orchestrator thread, just before a Reset; never blocks.</summary>
    void SetBufferWindow(int seconds)
    {
    }

    /// <summary>Builds a spec's encoder chain in advance for the next save press; null drops it. Never blocks.</summary>
    void PrepareChain(SessionSpec? next)
    {
    }

    /// <summary>A Clip of Rewind up to the press, not yet written; null when there is nothing to save. Throws when too short.</summary>
    IClipWrite? Save(long qpc) => null;

    /// <summary>A Recording from the press on this session's chain, not yet started; null when the session can't record now.</summary>
    IRecordingWrite? Record(long qpc) => null;

    /// <summary>How many encoder chains the session runs now; one being torn down doesn't count. Orchestrator thread.</summary>
    int Chains => 1;

    /// <summary>Switches to a spec's chain at a Reset, tearing the current one down first; false when it can't switch now.</summary>
    bool SwitchChain(SessionSpec next) => false;

    /// <summary>Starts one attempt, off the calling thread, to rebuild after a device loss; false when none is needed.</summary>
    bool RecoverDevice() => false;

    /// <summary>The PC is going to sleep: tears capture and chains down, keeping Rewind saveable; false when it can't.</summary>
    bool Suspend() => false;

    /// <summary>The GDI device name of the monitor a Monitor session captures; null for a game session.</summary>
    string? MonitorDevice => null;

    /// <summary>The GDI device names of every monitor a Monitor session still captures. Orchestrator thread; never blocks.</summary>
    IReadOnlyList<string> MonitorDevices => MonitorDevice is { } device ? [device] : [];

    /// <summary>Stops capturing a removed composited monitor: its area turns black. False when the session doesn't capture it.</summary>
    bool DropMonitor(string device) => false;

    /// <summary>Whether the session may build a chain ahead beside its current one; false while a Monitor Recording holds one.</summary>
    void SetBuildAhead(bool allowed)
    {
    }
}

/// <summary>The attached monitors and their changes, so the orchestrator can end a Monitor Recording whose monitor is gone.</summary>
public interface IDisplayMonitors
{
    /// <summary>Every attached monitor now, the primary first. Never throws; empty when they can't be read.</summary>
    IReadOnlyList<DisplayMonitor> List();

    /// <summary>Calls back after each display change, on any thread, until the subscription is disposed. Never throws.</summary>
    IDisposable Subscribe(Action changed);
}

/// <summary>The PC's sleep and resume; the orchestrator decides what a sleep stops and what a resume rebuilds.</summary>
public interface IPowerNotifications
{
    /// <summary>Calls back on each power change, on any thread, until the subscription is disposed. Never throws.</summary>
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

/// <summary>A Recording a session can write: one fragmented MP4 from the session's encoders.</summary>
public interface IRecordingWrite
{
    /// <summary>Starts writing a Recording to a reserved path, off the calling thread; done is called once when it ends.</summary>
    void Start(string path, Action<Exception?> done);

    /// <summary>Ends the Recording at the press, then finishes its file and calls the Start callback. Never blocks on the file.</summary>
    void Stop(long qpc);
}

/// <summary>A Clip a session can write.</summary>
public interface IClipWrite
{
    /// <summary>Writes the Clip to a reserved path, off the calling thread; done is called once, from the writer's thread.</summary>
    void Write(string path, Action<Exception?> done);

    /// <summary>The spec this write's press switched the session to, or null; it stands whatever the write's outcome.</summary>
    SessionSpec? SwitchesTo => null;

    /// <summary>Makes this Clip's end Rewind's Reset, so the next Clip starts where this one ended. Only after success.</summary>
    void Reset()
    {
    }
}

/// <summary>Where the orchestrator keeps mic mute between runs; it is runtime state, never a setting. Neither call throws.</summary>
public interface IMicMuteStore
{
    /// <summary>The saved mic mute; true (muted) when nothing is saved or the saved state can't be read.</summary>
    bool Load();

    /// <summary>Saves <paramref name="muted"/>; a failure is logged at Warning and the call returns.</summary>
    void Save(bool muted);
}

/// <summary>Where the orchestrator keeps the paths of Recordings being written, so a restart can trim those left open.</summary>
public interface IOpenRecordings
{
    /// <summary>A Recording at <paramref name="path"/> is about to be written.</summary>
    void Opened(string path);

    /// <summary>The Recording at <paramref name="path"/> is no longer being written, however it ended.</summary>
    void Closed(string path);
}

/// <summary>How a session posts back to the orchestrator from its media threads; every call only posts and returns.</summary>
public interface ISessionEvents
{
    /// <summary>The captured window closed.</summary>
    void Closed(string reason);

    /// <summary>Capture or encoding failed.</summary>
    void Failed(Exception failure);

    /// <summary>The session's encoder chain was forcibly rebuilt; a Recording on the old chain has ended at its last packet.</summary>
    void ChainRestarted(long qpc)
    {
    }

    /// <summary>The session's output anchored at its first frame; from now on a Recording can start.</summary>
    void Anchored(long qpc)
    {
    }

    /// <summary>The GPU device was removed or reset, or a recovery attempt failed; nothing is captured until one succeeds.</summary>
    void DeviceLost(Exception failure)
    {
    }
}

/// <summary>How a session ended: the finished file (null when none was written), why, and any failure.</summary>
public sealed record SessionOutcome(string? Path, string Reason, Exception? Failure);
