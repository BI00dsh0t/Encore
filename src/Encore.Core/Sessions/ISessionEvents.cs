namespace Encore.Core.Sessions;

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
