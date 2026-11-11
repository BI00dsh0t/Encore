namespace Encore.Core.Sessions;

/// <summary>Where the orchestrator keeps mic mute between runs; it is runtime state, never a setting. Neither call throws.</summary>
public interface IMicMuteStore
{
    /// <summary>The saved mic mute; true (muted) when nothing is saved or the saved state can't be read.</summary>
    bool Load();

    /// <summary>Saves <paramref name="muted"/>; a failure is logged at Warning and the call returns.</summary>
    void Save(bool muted);
}
