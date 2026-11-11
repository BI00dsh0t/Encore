namespace Encore.Core.Sessions;

/// <summary>What a proposed Target Game does to the committed one.</summary>
internal enum TargetDecision
{
    /// <summary>The same process: at most its window moves.</summary>
    SameProcess,

    /// <summary>A switch to another game while a Recording runs: held until the Recording's writer posts back.</summary>
    Held,

    /// <summary>No Target before and none proposed.</summary>
    Nothing,
    Switch,
}
