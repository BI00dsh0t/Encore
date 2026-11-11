namespace Encore.Platform.Windows;

/// <summary>A window that may be a game's, and the process that owns it.</summary>
public readonly record struct GameWindow(nint Handle, int ProcessId);
