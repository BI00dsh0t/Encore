using System.Windows.Input;
using Encore.App.Settings;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Library;

/// <summary>Where a welcome card link goes: the settings page it names.</summary>
internal enum WelcomeLink
{
    AudioTracks,
    Storage,
}

/// <summary>
/// The empty Library's welcome card (story 10.6, EXPERIENCE.md "Welcome and the empty Library"): what to
/// press and how much it saves, the audio being recorded, where Clips go, and how to get a Recording,
/// all from the live settings, each linking to its page. It shows only on an empty, unfiltered Library.
/// </summary>
internal sealed class LibraryWelcome : Observable
{
    private string _saveKey = "the Save Clip key";
    private string _clipLength = "";
    private string _audio = "";
    private bool _isSilent;
    private string _folder = "";
    private string _recordKey = "the Record key";

    public LibraryWelcome()
    {
        ChangeAudioCommand = new RelayCommand(() => Navigate?.Invoke(WelcomeLink.AudioTracks));
        ChangeFolderCommand = new RelayCommand(() => Navigate?.Invoke(WelcomeLink.Storage));
    }

    /// <summary>Opens a link's page; set by the shell.</summary>
    public Action<WelcomeLink>? Navigate { get; set; }

    /// <summary>The Save Hotkey, as the key-cap chip shows it: "Alt+F10".</summary>
    public string SaveKey
    {
        get => _saveKey;
        private set => Set(ref _saveKey, value);
    }

    /// <summary>The Clip length in words: "30 s".</summary>
    public string ClipLength
    {
        get => _clipLength;
        private set => Set(ref _clipLength, value);
    }

    /// <summary>The lead line after the key: "to save the last 30 s as a Clip".</summary>
    public string LeadTail => $"to save the last {ClipLength} as a Clip";

    /// <summary>"Audio: Speakers, Microphone", or the silent line when track 1 has no sounds.</summary>
    public string Audio
    {
        get => _audio;
        private set => Set(ref _audio, value);
    }

    public bool IsSilent
    {
        get => _isSilent;
        private set
        {
            if (Set(ref _isSilent, value))
            {
                OnPropertyChanged(nameof(AudioLink));
            }
        }
    }

    /// <summary>The audio line's link: Change, or Set up audio when it's silent.</summary>
    public string AudioLink => IsSilent ? "Set up audio" : "Change";

    /// <summary>"Clips are saved in {folder}".</summary>
    public string Folder
    {
        get => _folder;
        private set => Set(ref _folder, value);
    }

    /// <summary>The Recordings line, in caption.</summary>
    public string Recordings => $"Want the whole match? Allow Recordings for the game on its page in Games, then press {_recordKey}.";

    public ICommand ChangeAudioCommand { get; }

    public ICommand ChangeFolderCommand { get; }

    /// <summary>Takes the snapshot's settings and Clips folder.</summary>
    public void Show(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var settings = state.Settings;
        var hotkeys = SettingsValues.Global(settings, Keys.Hotkeys);
        SaveKey = hotkeys.Save is { } save ? HotkeysPage.Describe(save) : "the Save Clip key";
        ClipLength = HotkeysPage.ClipLength(SettingsValues.Global(settings, Keys.BufferWindow));
        OnPropertyChanged(nameof(LeadTail));

        var layout = SettingsValues.Global(settings, Keys.TrackLayout);
        var sources = layout.Tracks.Count == 0
            ? []
            : layout.Tracks[0].DeviceKeys.Select(k => layout.Devices.FirstOrDefault(d => d.Key == k)?.FriendlyName).OfType<string>().ToList();
        IsSilent = sources.Count == 0;
        Audio = IsSilent ? "Audio: none yet — your Clips will be silent" : "Audio: " + string.Join(", ", sources);
        Folder = $"Clips are saved in {(state.LibraryFolder is { Length: > 0 } folder ? folder : "your Videos folder")}";

        var record = SettingsValues.Global(settings, Keys.RecordHotkeyMode) == RecordHotkeyMode.Toggle ? hotkeys.RecordToggle : hotkeys.RecordStart;
        _recordKey = record is { } key ? HotkeysPage.Describe(key) : "the Record key";
        OnPropertyChanged(nameof(Recordings));
    }
}
