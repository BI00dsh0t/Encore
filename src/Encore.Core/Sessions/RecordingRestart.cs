namespace Encore.Core.Sessions;

/// <summary>A forced encoder restart's handover: the Recording it stopped and the one that took over, by path.</summary>
public sealed record RecordingRestart(string StoppedPath, string StartedPath);
