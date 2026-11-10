using System.Windows;
using Encore.App.Engine;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>The Global Hotkeys: each action's binding with the engine's status, grouped by purpose. UI thread.</summary>
internal sealed class HotkeysViewModel : SettingsViewModel
{
    private HotkeySettings _hotkeys = HotkeyBindings.None;
    private bool _fromDocument;
    private RecordHotkeyMode _mode = Keys.RecordHotkeyMode.Default;
    private HotkeyStatus _status = HotkeyStatus.Unbound;
    private int _clipSeconds = Keys.BufferWindow.Default;

    public HotkeysViewModel(SettingsSender sender, IEngineSession engine)
        : base(sender)
    {
        Rows = [.. Enum.GetValues<HotkeyAction>().Select(action => new HotkeyRow(this, action, HotkeyText.Label(action)))];
        Monitor = new MonitorPicker(engine, async ops => await SendAsync(ops), message => Message = message);
        ShowRows();
    }

    public override string Title => "Hotkeys";

    /// <summary>One row per action, in <see cref="HotkeyAction"/> order.</summary>
    public IReadOnlyList<HotkeyRow> Rows { get; }

    /// <summary>Clips: Save Clip.</summary>
    public IReadOnlyList<HotkeyRow> ClipRows => RowsOf(HotkeyAction.Save);

    /// <summary>Recordings: Record, or Record start and stop.</summary>
    public IReadOnlyList<HotkeyRow> RecordRows => RowsOf(HotkeyAction.RecordToggle, HotkeyAction.RecordStart, HotkeyAction.RecordStop);

    /// <summary>Monitor Recording: its key, or its start and stop keys.</summary>
    public IReadOnlyList<HotkeyRow> MonitorRows =>
        RowsOf(HotkeyAction.MonitorRecordToggle, HotkeyAction.MonitorRecordStart, HotkeyAction.MonitorRecordStop);

    /// <summary>Microphone: Mic mute.</summary>
    public IReadOnlyList<HotkeyRow> MicRows => RowsOf(HotkeyAction.MicMute);

    /// <summary>The screens a Monitor Recording captures, beside its key.</summary>
    public MonitorPicker Monitor { get; }

    /// <summary>The Clip sentence (EXPERIENCE.md "Clips and Recordings, explained").</summary>
    public string ClipsHelp =>
        $"The last {HotkeyText.ClipLength(_clipSeconds)} of your game, saved when you press {KeyName(HotkeyAction.Save, "Save Clip")}."
        + " Encore keeps that much ready all the time, so you never miss a moment.";

    /// <summary>The Recording sentence.</summary>
    public string RecordingsHelp
    {
        get
        {
            var key = KeyName(IsToggleMode ? HotkeyAction.RecordToggle : HotkeyAction.RecordStart, "the Record key");
            return $"One long video of your game that you start and stop yourself with {key}, alongside Clips."
                + " Allow it per game, on the game's page.";
        }
    }

    /// <summary>The Monitor Recording sentence.</summary>
    public string MonitorHelp
    {
        get
        {
            var action = IsToggleMode ? HotkeyAction.MonitorRecordToggle : HotkeyAction.MonitorRecordStart;
            return "A Recording of your screens instead of a game, for anything that isn't detected as a game."
                + $" Start and stop it with {KeyName(action, "the Monitor Recording key")}.";
        }
    }

    public string MicHelp => "Silences your microphone on the tracks you choose in Audio Tracks.";

    /// <summary>The row waiting for a combination, or null.</summary>
    public HotkeyRow? CapturingRow => Rows.FirstOrDefault(row => row.IsCapturing);

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

    /// <summary>Shows the screens a newly connected engine run lists, and the probe's codecs. Called before Render.</summary>
    public void ShowMonitors(StateSnapshot state) => Monitor.ShowState(state);

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
            _hotkeys = HotkeyBindings.FromStatus(status);
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
        if (!HotkeyBindings.CanStandAlone(binding))
        {
            Message = "Add Ctrl, Alt, Shift or Win; only F1 to F24 work alone.";
            return;
        }

        foreach (var other in Enum.GetValues<HotkeyAction>())
        {
            if (other != action && _hotkeys.For(other) is { } taken && HotkeyBindings.SameCombination(taken, binding))
            {
                Message = $"{HotkeyText.Describe(binding)} is already the {HotkeyText.Label(other)} Hotkey.";
                return;
            }
        }

        Send(HotkeyBindings.With(_hotkeys, action, binding));
    }

    /// <summary>Unbinds <paramref name="action"/> and sends the whole <c>hotkeys</c> value.</summary>
    public void Clear(HotkeyAction action)
    {
        CancelCapture();
        Send(HotkeyBindings.With(_hotkeys, action, null));
    }

    protected override FrameworkElement CreateView() => new HotkeysView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        _fromDocument = document.Global.TryGetValue(Keys.Hotkeys.Name, out var raw) && Keys.Hotkeys.TryRead(raw, out _);
        _hotkeys = _fromDocument ? SettingsValues.Global(document, Keys.Hotkeys) : HotkeyBindings.FromStatus(_status);
        _mode = SettingsValues.Global(document, Keys.RecordHotkeyMode);
        _clipSeconds = SettingsValues.Global(document, Keys.BufferWindow);
        OnPropertyChanged(nameof(IsToggleMode));
        OnPropertyChanged(nameof(IsSeparateMode));
        Monitor.Render(document);
        ShowRows();
    }

    private IReadOnlyList<HotkeyRow> RowsOf(params HotkeyAction[] actions) => [.. Rows.Where(row => actions.Contains(row.Action))];

    private string KeyName(HotkeyAction action, string unbound) =>
        _hotkeys.For(action) is { } binding ? HotkeyText.Describe(binding) : unbound;

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
            var status = _status.Bindings.FirstOrDefault(bound => bound.Action == row.Action);
            row.Show(
                HotkeyText.Describe(_hotkeys.For(row.Action)),
                HotkeyText.Status(status),
                HotkeyBindings.IsFailed(status),
                HotkeyBindings.IsUsed(row.Action, _mode));
        }
    }
}
