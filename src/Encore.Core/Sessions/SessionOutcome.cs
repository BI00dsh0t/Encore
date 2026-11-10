namespace Encore.Core.Sessions;

/// <summary>How a session ended: the finished file (null when none was written), why, and any failure.</summary>
public sealed record SessionOutcome(string? Path, string Reason, Exception? Failure);
