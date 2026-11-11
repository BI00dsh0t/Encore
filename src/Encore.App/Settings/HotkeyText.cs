using System.Globalization;
using System.Windows.Input;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>How the App words a Hotkey: its combination, its action, its status and the Clip length beside it.</summary>
internal static class HotkeyText
{
    /// <summary>A binding as the builder reads it, e.g. "Ctrl+Shift+F5"; "Not set" for none.</summary>
    public static string Describe(HotkeyBinding? binding)
    {
        if (binding is null)
        {
            return "Not set";
        }

        List<string> parts = [];
        AddIfHeld(parts, binding, HotkeyModifiers.Control, "Ctrl");
        AddIfHeld(parts, binding, HotkeyModifiers.Alt, "Alt");
        AddIfHeld(parts, binding, HotkeyModifiers.Shift, "Shift");
        AddIfHeld(parts, binding, HotkeyModifiers.Win, "Win");
        parts.Add(KeyName(binding.Vk));
        return string.Join("+", parts);
    }

    /// <summary>A Clip length in words: "30 s", "5 min", "1 min 30 s".</summary>
    public static string ClipLength(int seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        _ when seconds % 60 == 0 => $"{seconds / 60} min",
        _ => $"{seconds / 60} min {seconds % 60} s",
    };

    /// <summary>The action's name on its row.</summary>
    public static string Label(HotkeyAction action) => action switch
    {
        HotkeyAction.Save => "Save Clip",
        HotkeyAction.MicMute => "Mic mute",
        HotkeyAction.RecordToggle => "Record",
        HotkeyAction.RecordStart => "Record start",
        HotkeyAction.RecordStop => "Record stop",
        HotkeyAction.MonitorRecordToggle => "Monitor Recording",
        HotkeyAction.MonitorRecordStart => "Monitor Recording start",
        HotkeyAction.MonitorRecordStop => "Monitor Recording stop",
        _ => action.ToString(),
    };

    /// <summary>The engine's status of a binding, as its row shows it.</summary>
    public static string Status(HotkeyBindingStatus? status) => status switch
    {
        null => "Not set",
        { Registered: true } => "Working",
        { Code: ErrorCodes.HotkeyTaken } => "Failed: another app holds this combination",
        { Code: ErrorCodes.HotkeyDuplicate } => "Failed: another Hotkey has this combination",
        { Code: ErrorCodes.HotkeyInactive } => "Not used in this Record mode",
        { Code: ErrorCodes.HotkeyUnbound } => "Not set",
        _ => "Failed to register",
    };

    private static void AddIfHeld(List<string> parts, HotkeyBinding binding, int modifier, string name)
    {
        if ((binding.Modifiers & modifier) != 0)
        {
            parts.Add(name);
        }
    }

    // A digit key reads as its digit, and a key WPF doesn't know as its virtual-key code.
    private static string KeyName(int virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        return key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            Key.None => "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture),
            _ => key.ToString(),
        };
    }
}
