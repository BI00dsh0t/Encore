namespace Encore.Platform.Windows;

/// <summary>What Game Detection reads from the system; tests fake it.</summary>
public interface IGameWindowProbe
{
    /// <summary>The foreground window when it covers its monitor with no caption and isn't the shell's; otherwise null.</summary>
    GameWindow? ForegroundGameWindow();

    /// <summary>The foreground window, whatever its shape, or null when there is none or it is the shell's.</summary>
    GameWindow? ForegroundWindow();

    /// <summary>Visible, unowned, uncloaked top-level windows in Z order, minimized ones included; no tool or shell windows.</summary>
    IReadOnlyList<GameWindow> Windows();

    /// <summary>Opens a process with limited access; null when it can't be opened or its image path read.</summary>
    IGameProcess? Open(int processId);
}
