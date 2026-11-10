using System.Windows.Input;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>The rules of Hotkey bindings the App shares with the engine: which keys bind, which clash, which mode uses which.</summary>
internal static class HotkeyBindings
{
    private const int FirstFunctionKey = 0x70;
    private const int LastFunctionKey = 0x87;

    /// <summary>No action bound.</summary>
    public static HotkeySettings None { get; } = new(null, null, null, null, null, null);

    /// <summary>The binding a key press makes, with the held modifiers; null for a modifier alone.</summary>
    public static HotkeyBinding? FromKey(Key key, Key systemKey, ModifierKeys modifiers)
    {
        var pressed = key == Key.System ? systemKey : key;
        if (pressed is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return null;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(pressed);
        return virtualKey is < 1 or > 254 ? null : new HotkeyBinding(virtualKey, Flags(modifiers));
    }

    /// <summary>Whether the binding may be taken from every app: it has a modifier, or is one of F1 to F24.</summary>
    public static bool CanStandAlone(HotkeyBinding binding) =>
        (binding.Modifiers & ~HotkeyModifiers.NoRepeat) != 0 || binding.Vk is >= FirstFunctionKey and <= LastFunctionKey;

    /// <summary>Whether two bindings are the same key press; NoRepeat doesn't count, as in the engine.</summary>
    public static bool SameCombination(HotkeyBinding a, HotkeyBinding b) =>
        a.Vk == b.Vk && (a.Modifiers & ~HotkeyModifiers.NoRepeat) == (b.Modifiers & ~HotkeyModifiers.NoRepeat);

    /// <summary>The bindings the engine reports, as a settings value.</summary>
    public static HotkeySettings FromStatus(HotkeyStatus status) =>
        status.Bindings.Aggregate(None, (hotkeys, bound) => With(hotkeys, bound.Action, bound.Binding));

    /// <summary>The settings with one action's binding replaced.</summary>
    public static HotkeySettings With(HotkeySettings hotkeys, HotkeyAction action, HotkeyBinding? binding) => action switch
    {
        HotkeyAction.Save => hotkeys with { Save = binding },
        HotkeyAction.MicMute => hotkeys with { MicMute = binding },
        HotkeyAction.RecordToggle => hotkeys with { RecordToggle = binding },
        HotkeyAction.RecordStart => hotkeys with { RecordStart = binding },
        HotkeyAction.RecordStop => hotkeys with { RecordStop = binding },
        HotkeyAction.MonitorRecordToggle => hotkeys with { MonitorRecordToggle = binding },
        HotkeyAction.MonitorRecordStart => hotkeys with { MonitorRecordStart = binding },
        HotkeyAction.MonitorRecordStop => hotkeys with { MonitorRecordStop = binding },
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>Whether the Record mode uses the action: Toggle the toggle keys, Separate the start and stop keys.</summary>
    public static bool IsUsed(HotkeyAction action, RecordHotkeyMode mode) => action switch
    {
        HotkeyAction.RecordToggle or HotkeyAction.MonitorRecordToggle => mode == RecordHotkeyMode.Toggle,
        HotkeyAction.RecordStart or HotkeyAction.RecordStop or HotkeyAction.MonitorRecordStart or HotkeyAction.MonitorRecordStop =>
            mode == RecordHotkeyMode.Separate,
        _ => true,
    };

    /// <summary>Whether the engine tried to register the binding and couldn't.</summary>
    public static bool IsFailed(HotkeyBindingStatus? status) =>
        status is { Registered: false, Code: not (ErrorCodes.HotkeyInactive or ErrorCodes.HotkeyUnbound) };

    private static int Flags(ModifierKeys modifiers)
    {
        var flags = 0;
        flags |= modifiers.HasFlag(ModifierKeys.Alt) ? HotkeyModifiers.Alt : 0;
        flags |= modifiers.HasFlag(ModifierKeys.Control) ? HotkeyModifiers.Control : 0;
        flags |= modifiers.HasFlag(ModifierKeys.Shift) ? HotkeyModifiers.Shift : 0;
        flags |= modifiers.HasFlag(ModifierKeys.Windows) ? HotkeyModifiers.Win : 0;
        return flags;
    }
}
