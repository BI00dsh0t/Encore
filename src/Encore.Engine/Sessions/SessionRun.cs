namespace Encore.Engine.Sessions;

/// <summary>What the session itself knows of a finished run, for its stats report.</summary>
internal sealed record SessionRun(string Path, string WindowTitle, string? ProcessName, string Adapter, string StopReason, double Seconds);
