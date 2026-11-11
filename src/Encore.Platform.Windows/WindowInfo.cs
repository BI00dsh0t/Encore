namespace Encore.Platform.Windows;

/// <summary>A visible, titled top-level window.</summary>
public sealed record WindowInfo(nint Handle, string Title, uint ProcessId, string? ProcessName);
