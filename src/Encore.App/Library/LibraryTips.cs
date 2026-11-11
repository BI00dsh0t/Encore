using System.Windows.Input;
using Encore.App.Settings;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Library;

/// <summary>The Library's first-use tips: how to save a Clip and record a match, shown until the user closes them once.</summary>
internal sealed class LibraryTips : Observable
{
    private readonly string? _firstUseFile;
    private bool _closed;
    private bool _hasSnapshot;
    private string _saveKey = "";
    private string _clipLength = "";
    private string _recordKey = "";

    /// <summary>The tips remember their close in <paramref name="firstUseFile"/>; without one they stay hidden, as in tests.</summary>
    public LibraryTips(string? firstUseFile)
    {
        _firstUseFile = firstUseFile;
        _closed = firstUseFile is null || FirstUse.Read(firstUseFile).TipsClosed;
        CloseCommand = new RelayCommand(Close);
        ChangeHotkeysCommand = new RelayCommand(() => ChangeHotkeys?.Invoke());
    }

    /// <summary>Opens the Hotkeys page; set by the shell.</summary>
    public Action? ChangeHotkeys { get; set; }

    /// <summary>Shown once the first snapshot gives the real hotkeys, until the user closes the tips.</summary>
    public bool IsShown => _hasSnapshot && !_closed;

    /// <summary>The Save Hotkey: "Alt+F10", or "the Save Clip key" when none is set.</summary>
    public string SaveKey
    {
        get => _saveKey;
        private set => Set(ref _saveKey, value);
    }

    /// <summary>Rewind's length in words: "30 s".</summary>
    public string ClipLength
    {
        get => _clipLength;
        private set => Set(ref _clipLength, value);
    }

    /// <summary>The Record key: "Alt+F9", or "the Record key" when none is set.</summary>
    public string RecordKey
    {
        get => _recordKey;
        private set => Set(ref _recordKey, value);
    }

    public ICommand CloseCommand { get; }

    public ICommand ChangeHotkeysCommand { get; }

    /// <summary>Takes the snapshot's hotkeys and Rewind length.</summary>
    public void Show(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var settings = state.Settings;
        var hotkeys = SettingsValues.Global(settings, Keys.Hotkeys);
        SaveKey = hotkeys.Save is { } save ? KeepTogether(HotkeyText.Describe(save)) : "the Save Clip key";
        ClipLength = HotkeyText.ClipLength(SettingsValues.Global(settings, Keys.BufferWindow));
        var toggles = SettingsValues.Global(settings, Keys.RecordHotkeyMode) == RecordHotkeyMode.Toggle;
        var record = toggles ? hotkeys.RecordToggle : hotkeys.RecordStart;
        RecordKey = record is { } key ? KeepTogether(HotkeyText.Describe(key)) : "the Record key";
        if (!_hasSnapshot)
        {
            _hasSnapshot = true;
            OnPropertyChanged(nameof(IsShown));
        }
    }

    // Closing is for good: the file remembers it, so the tips never come back.
    private void Close()
    {
        _closed = true;
        if (_firstUseFile is not null)
        {
            new FirstUse(TipsClosed: true).Write(_firstUseFile);
        }

        OnPropertyChanged(nameof(IsShown));
    }

    // Word joiners around each "+", so a wrapped line never splits a hotkey into "Alt" and "+F9".
    private static string KeepTogether(string hotkey) => hotkey.Replace("+", "⁠+⁠", StringComparison.Ordinal);
}
