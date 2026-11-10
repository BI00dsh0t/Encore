using Encore.Contracts.Settings;

namespace Encore.Core.Hotkeys;

/// <summary>What to do with one Hotkey action: register its binding, or skip it for SkipCode.</summary>
public sealed record HotkeyPlanEntry(HotkeyAction Action, HotkeyBinding? Binding, string? SkipCode)
{
    /// <summary>Whether this action is to be registered.</summary>
    public bool Registers => SkipCode is null;
}
