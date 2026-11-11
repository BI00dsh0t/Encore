namespace Encore.Core.Sessions;

/// <summary>The window a session captures, its process and that process's Game Profile (null for Global).</summary>
public sealed record CaptureTarget(int ProcessId, nint Window, string? ProfileId);
