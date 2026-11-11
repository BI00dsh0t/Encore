namespace Encore.Media;

/// <summary>ReplacedHeld means the held frame is superseded by the one offered: release it.</summary>
public readonly record struct PacerDecision(PacerAction Action, long Slot, long Pts, bool ReplacedHeld = false);
