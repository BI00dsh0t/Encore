using System.Windows;
using System.Windows.Input;
using Encore.App.Engine;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>The Global Hotkeys: each action's binding with the engine's status, grouped by purpose. UI thread.</summary>
internal sealed class HotkeysViewModel : SettingsViewModel
{
    private HotkeySettings _hotkeys = NoBindings;
    private bool _fromDocument;
    private RecordHotkeyMode _mode = Keys.RecordHotkeyMode.Default;
    private HotkeyStatus _status = HotkeyStatus.Unbound;
    private int _clipSeconds = Keys.BufferWindow.Default;

    public HotkeysViewModel(SettingsSender sender, IEngineSession engine)
        : base(sender)
    {
        Rows = [.. Enum.GetValues<HotkeyAction>().Select(a => new HotkeyRow(this, a, Label(a)))];
        Monitor = new MonitorPicker(engine, async ops => await SendAsync(ops), message => Message = message);
        ShowRows();
    }

    public override string Title => "Hotkeys";

    /// <summary>One row per action, in <see cref="HotkeyAction"/> order.</summary>
    public IReadOnlyList<HotkeyRow> Rows { get; }

    /// <summary>Clips: Save Clip.</summary>
    public IReadOnlyList<HotkeyRow> ClipRows => [.. Rows.Where(r => r.Action == HotkeyAction.Save)];

    /// <summary>Recordings: Record, or Record start and stop.</summary>
    public IReadOnlyList<HotkeyRow> RecordRows => [.. Rows.Where(r => r.Action is HotkeyAction.RecordToggle or HotkeyAction.RecordStart or HotkeyAction.RecordStop)];

    /// <summary>Monitor Recording: its key, or its start and stop keys.</summary>
    public IReadOnlyList<HotkeyRow> MonitorRows => [.. Rows.Where(r => r.Action is HotkeyAction.MonitorRecordToggle or HotkeyAction.MonitorRecordStart or HotkeyAction.MonitorRecordStop)];

    /// <summary>Microphone: Mic mute.</summary>
    public IReadOnlyList<HotkeyRow> MicRows => [.. Rows.Where(r => r.Action == HotkeyAction.MicMute)];

    /// <summary>The screens a Monitor Recording captures, beside its key.</summary>
    public MonitorPicker Monitor { get; }

    /// <summary>The Clip sentence (EXPERIENCE.md "Clips and Recordings, explained").</summary>
    public string ClipsHelp =>
        $"The last {ClipLength(_clipSeconds)} of your game, saved when you press {KeyName(HotkeyAction.Save, "Save Clip")}. Encore keeps that much ready all the time, so you never miss a moment.";

    /// <summary>The Recording sentence.</summary>
    public string RecordingsHelp =>
        $"One long video of your game that you start and stop yourself with {KeyName(_mode == RecordHotkeyMode.Toggle ? HotkeyAction.RecordToggle : HotkeyAction.RecordStart, "the Record key")}, alongside Clips. Allow it per game, on the game's page.";

    /// <summary>The Monitor Recording sentence.</summary>
    public string MonitorHelp =>
        $"A Recording of your screens instead of a game, for anything that isn't detected as a game. Start and stop it with {KeyName(_mode == RecordHotkeyMode.Toggle ? HotkeyAction.MonitorRecordToggle : HotkeyAction.MonitorRecordStart, "the Monitor Recording key")}.";

    public string MicHelp => "Silences your microphone on the tracks you choose in Audio Tracks.";

    /// <summary>A Clip length in words: "30 s", "5 min", "1 min 30 s".</summary>
    internal static string ClipLength(int seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        _ when seconds % 60 == 0 => $"{seconds / 60} min",
        _ => $"{seconds / 60} min {seconds % 60} s",
    };

    /// <summary>Shows the screens a newly connected engine run lists, and the probe's codecs. Called before Render.</summary>
    public void ShowMonitors(StateSnapshot state) => Monitor.ShowState(state);

    /// <summary>The row waiting for a combination, or null.</summary>
    public HotkeyRow? CapturingRow => Rows.FirstOrDefault(r => r.IsCapturing);

    /// <summary><c>recordHotkeyMode</c> is Toggle.</summary>
    public bool IsToggleMode
    {
        get => _mode == RecordHotkeyMode.Toggle;
        set
        {
            if (value)
            {
                SendMode(RecordHotkeyMode.Toggle);
            }
        }
    }

    /// <summary><c>recordHotkeyMode</c> is Separate.</summary>
    public bool IsSeparateMode
    {
        get => _mode == RecordHotkeyMode.Separate;
        set
        {
            if (value)
            {
                SendMode(RecordHotkeyMode.Separate);
            }
        }
    }

    private static HotkeySettings NoBindings { get; } = new(null, null, null, null, null, null);

    /// <summary>Takes the engine's <c>hotkey.status</c>. Called before <see cref="SettingsViewModel.Render"/>.</summary>
    public void ShowStatus(HotkeyStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (Equals(_status, status))
        {
            return;
        }

        _status = status;

        // Without a document value, the engine's effective bindings are what the builder edits.
        if (!_fromDocument)
        {
            _hotkeys = FromStatus(status);
        }

        ShowRows();
    }

    /// <summary>Starts waiting for a combination on <paramref name="action"/>'s row, and stops on the others.</summary>
    public void StartCapture(HotkeyAction action)
    {
        foreach (var row in Rows)
        {
            row.IsCapturing = row.Action == action;
        }
    }

    /// <summary>Stops waiting for a combination.</summary>
    public void CancelCapture()
    {
        foreach (var row in Rows)
        {
            row.IsCapturing = false;
        }
    }

    /// <summary>Binds an action and sends the whole <c>hotkeys</c> value; refused when another action has the combination.</summary>
    public void Capture(HotkeyAction action, HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        CancelCapture();

        // A plain key would be taken from every app; only F1 to F24 may stand alone.
        if ((binding.Modifiers & ~HotkeyModifiers.NoRepeat) == 0 && binding.Vk is < 0x70 or > 0x87)
        {
            Message = "Add Ctrl, Alt, Shift or Win; only F1 to F24 work alone.";
            return;
        }

        foreach (var other in Enum.GetValues<HotkeyAction>())
        {
            if (other != action && _hotkeys.For(other) is { } taken && SameCombination(taken, binding))
            {
                Message = $"{Describe(binding)} is already the {Label(other)} Hotkey.";
                return;
            }
        }

        Send(With(_hotkeys, action, binding));
    }

    /// <summary>Unbinds <paramref name="action"/> and sends the whole <c>hotkeys</c> value.</summary>
    public void Clear(HotkeyAction action)
    {
        CancelCapture();
        Send(With(_hotkeys, action, null));
    }

    /// <summary>The binding a key press makes, with the held modifiers; null for a modifier alone.</summary>
    public static HotkeyBinding? FromKey(Key key, Key systemKey, ModifierKeys modifiers)
    {
        var pressed = key == Key.System ? systemKey : key;
        if (pressed is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return null;
        }

        var vk = KeyInterop.VirtualKeyFromKey(pressed);
        if (vk is < 1 or > 254)
        {
            return null;
        }

        var flags = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            flags |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            flags |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            flags |= HotkeyModifiers.Shift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            flags |= HotkeyModifiers.Win;
        }

        return new HotkeyBinding(vk, flags);
    }

    /// <summary>A binding as the builder reads it, e.g. "Ctrl+Shift+F5"; "Not set" for none.</summary>
    public static string Describe(HotkeyBinding? binding)
    {
        if (binding is null)
        {
            return "Not set";
        }

        List<string> parts = [];
        if ((binding.Modifiers & HotkeyModifiers.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((binding.Modifiers & HotkeyModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((binding.Modifiers & HotkeyModifiers.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((binding.Modifiers & HotkeyModifiers.Win) != 0)
        {
            parts.Add("Win");
        }

        var key = KeyInterop.KeyFromVirtualKey(binding.Vk);
        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Key.None => "0x" + binding.Vk.ToString("X2", System.Globalization.CultureInfo.InvariantCulture),
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }

    internal static string Label(HotkeyAction action) => action switch
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

    protected override FrameworkElement CreateView() => new HotkeysView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        _fromDocument = document.Global.TryGetValue(Keys.Hotkeys.Name, out var raw) && Keys.Hotkeys.TryRead(raw, out _);
        _hotkeys = _fromDocument ? SettingsValues.Global(document, Keys.Hotkeys) : FromStatus(_status);
        _mode = SettingsValues.Global(document, Keys.RecordHotkeyMode);
        _clipSeconds = SettingsValues.Global(document, Keys.BufferWindow);
        OnPropertyChanged(nameof(IsToggleMode));
        OnPropertyChanged(nameof(IsSeparateMode));
        Monitor.Render(document);
        ShowRows();
    }

    private string KeyName(HotkeyAction action, string unbound) => _hotkeys.For(action) is { } binding ? Describe(binding) : unbound;

    // Engine rule (HotkeyPlan.SameCombination): NoRepeat doesn't count.
    private static bool SameCombination(HotkeyBinding a, HotkeyBinding b) =>
        a.Vk == b.Vk && (a.Modifiers & ~HotkeyModifiers.NoRepeat) == (b.Modifiers & ~HotkeyModifiers.NoRepeat);

    private static HotkeySettings FromStatus(HotkeyStatus status) =>
        status.Bindings.Aggregate(NoBindings, (hotkeys, b) => With(hotkeys, b.Action, b.Binding));

    private static HotkeySettings With(HotkeySettings hotkeys, HotkeyAction action, HotkeyBinding? binding) => action switch
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

    private static string StatusText(HotkeyBindingStatus? status) => status switch
    {
        null => "Not set",
        { Registered: true } => "Working",
        { Code: ErrorCodes.HotkeyTaken } => "Failed: another app holds this combination",
        { Code: ErrorCodes.HotkeyDuplicate } => "Failed: another Hotkey has this combination",
        { Code: ErrorCodes.HotkeyInactive } => "Not used in this Record mode",
        { Code: ErrorCodes.HotkeyUnbound } => "Not set",
        _ => "Failed to register",
    };

    private static bool IsFailedStatus(HotkeyBindingStatus? status) =>
        status is { Registered: false, Code: not (ErrorCodes.HotkeyInactive or ErrorCodes.HotkeyUnbound) };

    private void Send(HotkeySettings hotkeys)
    {
        if (hotkeys == _hotkeys)
        {
            return;
        }

        _hotkeys = hotkeys;
        _fromDocument = true;
        ShowRows();
        _ = SendAsync([SettingsValues.SetGlobal(Keys.Hotkeys, hotkeys)]);
    }

    private void SendMode(RecordHotkeyMode mode)
    {
        if (mode == _mode)
        {
            return;
        }

        _mode = mode;
        OnPropertyChanged(nameof(IsToggleMode));
        OnPropertyChanged(nameof(IsSeparateMode));
        ShowRows();
        _ = SendAsync([SettingsValues.SetGlobal(Keys.RecordHotkeyMode, mode)]);
    }

    private void ShowRows()
    {
        OnPropertyChanged(nameof(ClipsHelp));
        OnPropertyChanged(nameof(RecordingsHelp));
        OnPropertyChanged(nameof(MonitorHelp));
        foreach (var row in Rows)
        {
            var status = _status.Bindings.FirstOrDefault(b => b.Action == row.Action);
            row.Show(Describe(_hotkeys.For(row.Action)), StatusText(status), IsFailedStatus(status), IsUsed(row.Action, _mode));
        }
    }

    // Engine rule (HotkeyPlan.IsActive): Toggle uses the toggle rows, Separate the start and stop rows.
    private static bool IsUsed(HotkeyAction action, RecordHotkeyMode mode) => action switch
    {
        HotkeyAction.RecordToggle or HotkeyAction.MonitorRecordToggle => mode == RecordHotkeyMode.Toggle,
        HotkeyAction.RecordStart or HotkeyAction.RecordStop or HotkeyAction.MonitorRecordStart or HotkeyAction.MonitorRecordStop => mode == RecordHotkeyMode.Separate,
        _ => true,
    };
}
