using Encore.Contracts;
using Encore.Contracts.Settings;
using Encore.Core.Settings;

namespace Encore.Core.Hotkeys;

/// <summary>Which Hotkeys to register, from the settings alone. Pure; every action appears once, in enum order.</summary>
public static class HotkeyPlan
{
    /// <summary>Every action, in registration order.</summary>
    public static IReadOnlyList<HotkeyAction> Actions { get; } = Enum.GetValues<HotkeyAction>();

    /// <summary>The plan for a document's Global Hotkeys; with none set, the catalog default applies only when asked for.</summary>
    public static IReadOnlyList<HotkeyPlanEntry> For(SettingsDocument document, bool useCatalogDefault)
    {
        ArgumentNullException.ThrowIfNull(document);

        HotkeySettings? hotkeys = document.Global.TryGetValue(Keys.Hotkeys.Name, out var raw) && Keys.Hotkeys.TryRead(raw, out var read)
            ? read
            : useCatalogDefault ? Keys.Hotkeys.Default : null;
        return For(hotkeys, SettingsResolver.Resolve(document, null).RecordHotkeyMode);
    }

    /// <summary>The plan for these Hotkeys in a mode: an action is skipped when unbound, unused by the mode, or a duplicate.</summary>
    public static IReadOnlyList<HotkeyPlanEntry> For(HotkeySettings? hotkeys, RecordHotkeyMode mode)
    {
        var entries = new List<HotkeyPlanEntry>(Actions.Count);
        var taken = new List<HotkeyBinding>();
        foreach (var action in Actions)
        {
            var binding = hotkeys?.For(action);
            string? skip;
            if (binding is null)
            {
                skip = ErrorCodes.HotkeyUnbound;
            }
            else if (!IsActive(action, mode))
            {
                skip = ErrorCodes.HotkeyInactive;
            }
            else if (taken.Any(t => SameCombination(t, binding)))
            {
                skip = ErrorCodes.HotkeyDuplicate;
            }
            else
            {
                skip = null;
                taken.Add(binding);
            }

            entries.Add(new HotkeyPlanEntry(action, binding, skip));
        }

        return entries;
    }

    /// <summary>Whether a mode uses an action: toggle mode uses the toggles, separate mode the start and stop actions.</summary>
    public static bool IsActive(HotkeyAction action, RecordHotkeyMode mode) => action switch
    {
        HotkeyAction.RecordToggle or HotkeyAction.MonitorRecordToggle => mode == RecordHotkeyMode.Toggle,
        HotkeyAction.RecordStart or HotkeyAction.RecordStop or HotkeyAction.MonitorRecordStart or HotkeyAction.MonitorRecordStop => mode == RecordHotkeyMode.Separate,
        _ => true,
    };

    /// <summary>Whether two bindings are one key combination; <see cref="HotkeyModifiers.NoRepeat"/> doesn't count.</summary>
    public static bool SameCombination(HotkeyBinding a, HotkeyBinding b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Vk == b.Vk && (a.Modifiers & ~HotkeyModifiers.NoRepeat) == (b.Modifiers & ~HotkeyModifiers.NoRepeat);
    }

    /// <summary>The first pair of actions bound to one combination, in enum order; null when every binding is distinct.</summary>
    public static (HotkeyAction First, HotkeyAction Second)? FindDuplicate(HotkeySettings hotkeys)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        for (var i = 0; i < Actions.Count; i++)
        {
            if (hotkeys.For(Actions[i]) is not { } first)
            {
                continue;
            }

            for (var j = i + 1; j < Actions.Count; j++)
            {
                if (hotkeys.For(Actions[j]) is { } second && SameCombination(first, second))
                {
                    return (Actions[i], Actions[j]);
                }
            }
        }

        return null;
    }
}
