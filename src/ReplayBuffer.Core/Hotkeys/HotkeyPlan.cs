using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Settings;

namespace ReplayBuffer.Core.Hotkeys;

/// <summary>
/// What to do with one <see cref="HotkeyAction"/>: register <see cref="Binding"/>, or skip it for
/// <see cref="SkipCode"/>. <see cref="Binding"/> is the configured one, or null when unbound.
/// </summary>
public sealed record HotkeyPlanEntry(HotkeyAction Action, HotkeyBinding? Binding, string? SkipCode)
{
    /// <summary>Whether this action is to be registered.</summary>
    public bool Registers => SkipCode is null;
}

/// <summary>
/// Which Hotkeys to register (AD-11), from the settings alone. Pure: no Win32, no state. Every
/// action appears once, in <see cref="HotkeyAction"/> order.
/// </summary>
public static class HotkeyPlan
{
    /// <summary>Every action, in registration order.</summary>
    public static IReadOnlyList<HotkeyAction> Actions { get; } = Enum.GetValues<HotkeyAction>();

    /// <summary>
    /// The plan for <paramref name="document"/>'s Global <c>hotkeys</c> and <c>recordHotkeyMode</c>.
    /// With no valid <c>hotkeys</c> in the document, the catalog default applies only when
    /// <paramref name="useCatalogDefault"/> (the installed instance); otherwise nothing is bound (AD-16).
    /// </summary>
    public static IReadOnlyList<HotkeyPlanEntry> For(SettingsDocument document, bool useCatalogDefault)
    {
        ArgumentNullException.ThrowIfNull(document);

        HotkeySettings? hotkeys = document.Global.TryGetValue(Keys.Hotkeys.Name, out var raw) && Keys.Hotkeys.TryRead(raw, out var read)
            ? read
            : useCatalogDefault ? Keys.Hotkeys.Default : null;
        return For(hotkeys, SettingsResolver.Resolve(document, null).RecordHotkeyMode);
    }

    /// <summary>
    /// The plan for <paramref name="hotkeys"/> (null: none bound) in <paramref name="mode"/>. An unbound
    /// action is <see cref="ErrorCodes.HotkeyUnbound"/>, whatever the mode; a bound one the mode doesn't
    /// use is <see cref="ErrorCodes.HotkeyInactive"/>; and one whose combination an earlier action
    /// already takes is <see cref="ErrorCodes.HotkeyDuplicate"/>.
    /// </summary>
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

    /// <summary>
    /// Whether <paramref name="mode"/> uses <paramref name="action"/>: toggle uses recordToggle and
    /// monitorRecordToggle, separate recordStart, recordStop, monitorRecordStart and monitorRecordStop.
    /// </summary>
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

    /// <summary>
    /// The first pair of actions, in <see cref="HotkeyAction"/> order, bound to one combination,
    /// whatever the mode; null when every binding is distinct.
    /// </summary>
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
