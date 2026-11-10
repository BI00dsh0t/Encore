namespace Encore.Core.Sessions;

/// <summary>A started session: its spec as of Start, and a task that completes with its outcome however it ends.</summary>
public sealed record RunningSession(SessionSpec Spec, Task<SessionOutcome> Ended);
