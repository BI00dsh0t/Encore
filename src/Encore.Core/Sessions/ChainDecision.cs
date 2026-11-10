namespace Encore.Core.Sessions;

/// <summary>What a game session does when its settings are resolved again.</summary>
internal enum ChainDecision
{
    /// <summary>The settings ask for the chain it has: a chain prepared for an older change is dropped.</summary>
    Keep,

    /// <summary>A Recording keeps the session on one chain until its writer posts back; the change is deferred.</summary>
    HeldByRecording,

    /// <summary>A Monitor Recording holds the second chain, so nothing is built ahead: the session switches at its next Reset.</summary>
    SwitchAtReset,

    /// <summary>The new chain is built ahead, and the next Clip switches to it.</summary>
    PrepareAhead,
}
