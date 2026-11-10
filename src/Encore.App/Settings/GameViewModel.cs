using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Encore.App.Engine;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>A game's page, opened from the Games list: rename, Capture, the game's own settings, and its files. UI thread.</summary>
internal sealed class GameViewModel : SettingsViewModel
{
    private readonly GameProfilesViewModel _parent;
    private readonly IGameShell _shell;
    private string _name = "";
    private bool _ignored;
    private bool _recordingsEnabled;
    private bool _isManual;
    private string? _folder;
    private string? _clipsFolder;
    private string _recordKey = "the Record key";
    private System.Windows.Media.ImageSource? _icon;

    public GameViewModel(SettingsSender sender, IEngineSession engine, string profileId, GameProfilesViewModel parent, IGameShell shell)
        : base(sender)
    {
        ProfileId = profileId;
        _parent = parent;
        _shell = shell;
        Output = new RewindSettingsViewModel(sender, profileId);
        Tracks = new TrackLayoutViewModel(sender, engine, profileId);
        Output.PropertyChanged += ChildChanged;
        Tracks.PropertyChanged += ChildChanged;
        Clips = new InheritedSetting(
            "Clips",
            () => Output.BufferWindowUsesGlobal = false,
            () => Reset("Clip length", () => Output.BufferWindowUsesGlobal = true),
            () => parent.EditMain?.Invoke(Keys.BufferWindow),
            () => new GameClipsEditor { DataContext = Output });
        Video = new InheritedSetting(
            "Video quality",
            () => Output.VideoUsesGlobal = false,
            () => Reset("video quality", () => Output.VideoUsesGlobal = true),
            () => parent.EditMain?.Invoke(Keys.Video),
            () => new GameVideoEditor { DataContext = Output });
        AudioTracks = new InheritedSetting(
            "Audio tracks",
            () => Tracks.UsesGlobal = false,
            () => Reset("audio tracks", () => Tracks.UsesGlobal = true),
            () => parent.EditMain?.Invoke(Keys.TrackLayout),
            () => Tracks.View);
        ShowProgramCommand = new RelayCommand(() => _shell.ShowProgram(ProfileId));
        OpenFolderCommand = new RelayCommand(() => _shell.OpenFolder(FolderPath!), () => FolderPath is not null);
        StopDetectingCommand = new RelayCommand(StopDetecting);
        Confirm = (message, caption) => Prompts.YesNo(_parent.View, message, caption, MessageBoxImage.Question);
        shell.LoadIcon(profileId, icon => Icon = icon);
    }

    /// <summary>The program's icon from the shell, or null for the letter tile.</summary>
    public System.Windows.Media.ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (Set(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => _icon is not null;

    /// <summary>The game's name, as the breadcrumb shows it.</summary>
    public override string Title => _name;

    /// <summary>The profile id: the program's canonical path, shown only on this page.</summary>
    public string ProfileId { get; }

    /// <summary>The program's location.</summary>
    public string ProgramPath => ProfileId;

    /// <summary>Clip length and Video quality's editor: a game-scoped Clips page, its view never shown.</summary>
    public RewindSettingsViewModel Output { get; }

    /// <summary>The game's audio tracks: the Audio Tracks editor, scoped to the game.</summary>
    public TrackLayoutViewModel Tracks { get; }

    public InheritedSetting Clips { get; }

    public InheritedSetting Video { get; }

    public InheritedSetting AudioTracks { get; }

    /// <summary>Asks a Yes/No question: Reset to the main setting. Tests replace it.</summary>
    public Func<string, string, bool> Confirm { get; set; }

    /// <summary>The name; a non-blank one that differs is sent trimmed, a blank one shows the old name again.</summary>
    public string Name
    {
        get => _name;
        set
        {
            var name = value?.Trim() ?? "";
            if (name.Length == 0 || name == _name)
            {
                OnPropertyChanged();
                return;
            }

            _name = name;
            NameChanged();
            SendAttribute(ProfileAttributes.Name, JsonSerializer.SerializeToElement(name, SettingsJsonContext.Default.String));
        }
    }

    /// <summary>The letter of the generic tile.</summary>
    public string Initial => _name.Length > 0 ? char.ToUpperInvariant(_name[0]).ToString() : "?";

    /// <summary>"Capture this game": <c>ignored</c>, inverted.</summary>
    public bool Capture
    {
        get => !_ignored;
        set
        {
            if (value == !_ignored)
            {
                return;
            }

            _ignored = !value;
            OnPropertyChanged();
            SendAttribute(ProfileAttributes.Ignored, JsonSerializer.SerializeToElement(_ignored, SettingsJsonContext.Default.Boolean));
        }
    }

    /// <summary>"Allow Recordings in this game": <c>recordingsEnabled</c>.</summary>
    public bool RecordingsEnabled
    {
        get => _recordingsEnabled;
        set
        {
            if (value == _recordingsEnabled)
            {
                return;
            }

            _recordingsEnabled = value;
            OnPropertyChanged();
            SendAttribute(ProfileAttributes.RecordingsEnabled, JsonSerializer.SerializeToElement(value, SettingsJsonContext.Default.Boolean));
        }
    }

    /// <summary>The Recording sentence, with the Record key.</summary>
    public string RecordingsHelp => $"One long video of your game that you start and stop yourself with {_recordKey}, alongside Clips.";

    /// <summary>Whether the program is in <c>manualGames</c>.</summary>
    public bool IsManual => _isManual;

    public string Source => _isManual ? "Added by hand" : "Detected automatically";

    /// <summary>The game's folder, assigned once by the engine, or null before its first save.</summary>
    public string? FolderPath => _folder is null || _clipsFolder is null ? null : Path.Combine(_clipsFolder, _folder);

    public string FolderText => FolderPath ?? "Made the first time this game saves a Clip or Recording.";

    /// <summary>An example file name from the game's current name: "Valorant_2026-11-04_21-03-11.mp4".</summary>
    public string FileNameExample => $"{SafeName(_name)}_{DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.mp4";

    /// <summary>Whether the game's audio tracks hold an unsaved draft: the row and nav dot.</summary>
    public override bool HasUnsavedChanges => Tracks.IsDirty;

    public ICommand ShowProgramCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public ICommand StopDetectingCommand { get; }

    /// <summary>Whether the page is on screen: the audio tracks editor meters only then.</summary>
    public bool IsOpen
    {
        get => Tracks.IsOpen;
        set => Tracks.IsOpen = value;
    }

    /// <summary>Takes the snapshot's devices, meters, video options and Clips folder. Called before rendering.</summary>
    public void ShowState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Tracks.ShowDevices(state.Devices);
        Tracks.ShowMeters(state.Meters, state.EngineInstanceId);
        Output.ShowState(state);
        if (_clipsFolder != state.LibraryFolder)
        {
            _clipsFolder = state.LibraryFolder;
            FolderChanged();
        }
    }

    /// <summary>The engine's file-name sanitizer, repeated here for the example only.</summary>
    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsControl(c) || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        var safe = builder.ToString().Trim().TrimEnd('.', ' ');
        return safe.Length == 0 || safe.All(c => c == '_') ? "Game" : safe;
    }

    protected override FrameworkElement CreateView() => new GamePageView { DataContext = this };

    protected override void OnRender(SettingsDocument document)
    {
        if (!document.Profiles.TryGetValue(ProfileId, out var profile))
        {
            return;
        }

        if (_name != profile.Name)
        {
            _name = profile.Name;
            NameChanged();
        }

        Set(ref _ignored, profile.Ignored, nameof(Capture));
        Set(ref _recordingsEnabled, profile.RecordingsEnabled, nameof(RecordingsEnabled));
        if (_folder != profile.Folder)
        {
            _folder = profile.Folder;
            FolderChanged();
        }

        var manual = SettingsValues.Global(document, Keys.ManualGames).Any(p => string.Equals(p, ProfileId, StringComparison.OrdinalIgnoreCase));
        if (Set(ref _isManual, manual, nameof(IsManual)))
        {
            OnPropertyChanged(nameof(Source));
        }

        var hotkeys = SettingsValues.Global(document, Keys.Hotkeys);
        var record = SettingsValues.Global(document, Keys.RecordHotkeyMode) == RecordHotkeyMode.Toggle ? hotkeys.RecordToggle : hotkeys.RecordStart;
        _recordKey = record is { } key ? HotkeysViewModel.Describe(key) : "the Record key";
        OnPropertyChanged(nameof(RecordingsHelp));

        Output.Render(document);
        Tracks.Render(document);
        var buffer = SettingsValues.Global(document, Keys.BufferWindow);
        Clips.Show(SettingsValues.HasOverride(document, Keys.BufferWindow, ProfileId), $"Save the last {buffer} seconds");
        Video.Show(SettingsValues.HasOverride(document, Keys.Video, ProfileId), VideoSummary(SettingsValues.Global(document, Keys.Video)));
        AudioTracks.Show(SettingsValues.HasOverride(document, Keys.TrackLayout, ProfileId), string.Join(Environment.NewLine, Tracks.Summary));
    }

    /// <summary>"H.264 · 1920 × 1080 · 60 fps · 20,000 kbps"; "Source" in place of the size under <c>matchSource</c>.</summary>
    internal static string VideoSummary(VideoSettings video) => string.Create(
        CultureInfo.InvariantCulture,
        $"{video.Codec switch { VideoCodec.H264 => "H.264", VideoCodec.Hevc => "HEVC", VideoCodec.Av1 => "AV1", var c => c.ToString() }} · {(video.MatchSource ? "Source" : $"{video.Width} × {video.Height}")} · {video.Fps} fps · {video.BitrateKbps:N0} kbps{(video.RateControl == RateControl.Vbr ? " · variable" : "")}");

    private void Reset(string section, Action reset)
    {
        if (Confirm($"Reset {_name}'s {section} to the main setting?", "Reset to the main setting"))
        {
            reset();
        }
    }

    private void StopDetecting()
    {
        var manual = SettingsValues.Global(Document, Keys.ManualGames);
        IReadOnlyList<string> next = [.. manual.Where(p => !string.Equals(p, ProfileId, StringComparison.OrdinalIgnoreCase))];
        if (next.Count != manual.Count)
        {
            _ = SendAsync([SettingsValues.SetGlobal(Keys.ManualGames, next)]);
        }
    }

    private void SendAttribute(string attribute, JsonElement value) =>
        _ = SendAsync([SettingsOp.SetProfile(ProfileId, attribute, value)]);

    private void NameChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FileNameExample));
    }

    private void FolderChanged()
    {
        OnPropertyChanged(nameof(FolderPath));
        OnPropertyChanged(nameof(FolderText));
        OpenFolderCommand.RaiseCanExecuteChanged();
    }

    // The editors' notes show on the page's error bar; a draft's dirtiness is the page's.
    private void ChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Message) && sender is SettingsViewModel viewModel)
        {
            Message = viewModel.Message;
        }
        else if (e.PropertyName == nameof(TrackLayoutViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
    }
}
