namespace Encore.Core.Sessions;

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
