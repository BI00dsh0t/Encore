using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Encore.App.Engine;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>Audio Tracks: the Global layout, or one game's, edited as one card per Track, with live meters. UI thread.</summary>
internal sealed class TrackLayoutViewModel : SettingsViewModel
{
    public const string PageTitle = "Audio Tracks";
    public const string NameNeeded = "Give every track a name to save.";
    public const string ChangedElsewhere = "Audio tracks were changed somewhere else, so this page shows the new ones.";

    private readonly MeterWatch _meters;
    private AudioDeviceList _devices = AudioDeviceList.Empty;
    private TrackLayout _basis = Keys.TrackLayout.Default;
    private AudioTracksDraft _draft = AudioTracksDraft.From(Keys.TrackLayout.Default);
    private bool _isDirty;
    private bool _saving;
    private bool _isOpen;
    private bool _usesGlobal;
    private string? _saveProblem;
    private FrameworkElement? _view;

    public TrackLayoutViewModel(SettingsSender sender, IEngineSession engine, string? profileId = null)
        : base(sender)
    {
        ProfileId = profileId;
        Picker = new SoundPicker(this);
        _meters = new MeterWatch(engine, () => IsOpen, Watched);
        _usesGlobal = profileId is not null;
        AddTrackCommand = new RelayCommand(AddTrack);
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        DiscardCommand = new RelayCommand(Discard);
        ApplyPresetCommand = new ParameterCommand(
            parameter => ApplyPreset(parameter is TrackLayoutPreset preset ? preset : TrackLayoutPreset.Simple));
        Confirm = (message, caption) => Prompts.Ask(_view, message, caption);
        Load(_basis);
    }

    public override string Title => PageTitle;

    /// <summary>The game whose own tracks this page edits; null on the main page.</summary>
    public string? ProfileId { get; }

    public bool IsProfile => ProfileId is not null;

    /// <summary>Whether the game uses the main tracks; unticking copies them as they are, so Track ids and device keys stay.</summary>
    public bool UsesGlobal
    {
        get => _usesGlobal;
        set
        {
            if (!IsProfile || value == _usesGlobal)
            {
                return;
            }

            ShowUsesGlobal(value);
            var global = SettingsValues.Global(Document, Keys.TrackLayout);
            if (value)
            {
                Message = null;
                _basis = global;
                Load(global);
            }

            _ = SendAsync([SettingsValues.UseGlobal(Keys.TrackLayout, ProfileId!, value, global)]);
        }
    }

    /// <summary>Whether the draft can be edited: always on the main page, else while the game has its own tracks.</summary>
    public bool CanEdit => !IsProfile || !UsesGlobal;

    /// <summary>The read-only summary mode: a game using the main tracks shows them as text.</summary>
    public bool ShowsSummary => !CanEdit;

    /// <summary>The Tracks and their sounds as text, one line per Track: "1  Everything: Speakers, Microphone".</summary>
    public IReadOnlyList<string> Summary => [.. Tracks.Select(SummaryLine)];

    /// <summary>One card per Track, in file order.</summary>
    public ObservableCollection<AudioTrackCard> Tracks { get; } = [];

    public bool HasNoTracks => Tracks.Count == 0;

    /// <summary>Whether the draft differs from the tracks the page rendered.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (Set(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
            }
        }
    }

    /// <inheritdoc/>
    public override bool HasUnsavedChanges => IsDirty;

    /// <summary>Why the last Save changes didn't save, on the save bar; null when it did or none was tried.</summary>
    public string? SaveProblem
    {
        get => _saveProblem;
        private set
        {
            if (Set(ref _saveProblem, value))
            {
                OnPropertyChanged(nameof(HasSaveProblem));
            }
        }
    }

    public bool HasSaveProblem => SaveProblem is not null;

    /// <summary>Whether the page is on screen; opening watches the draft's meters, closing unwatches and zeroes every level.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!Set(ref _isOpen, value))
            {
                return;
            }

            if (value)
            {
                _meters.PageOpened();
            }
            else
            {
                Picker.Close();
                _meters.PageClosed();
            }

            ShowLevels();
        }
    }

    /// <summary>Asks a Yes/No question: the preset and Remove track confirmations. Tests replace it.</summary>
    public Func<string, string, bool> Confirm { get; set; }

    public ICommand AddTrackCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand DiscardCommand { get; }

    /// <summary>Takes a <see cref="TrackLayoutPreset"/>; replaces the draft after its confirmation.</summary>
    public ICommand ApplyPresetCommand { get; }

    /// <summary>The sound picker a Track's Add a sound opens.</summary>
    public SoundPicker Picker { get; }

    /// <summary>The device list of the last snapshot.</summary>
    internal AudioDeviceList Devices => _devices;

    /// <summary>The presets, with their menu labels.</summary>
    public IReadOnlyList<AudioPresetChoice> Presets { get; } =
    [
        new(TrackLayoutPreset.Simple, "Simple", "Simple — one track with your PC's sound and your microphone"),
        new(
            TrackLayoutPreset.ForEditing,
            "For editing",
            "For editing — everything, game and chat, and your microphone on their own tracks"),
    ];

    /// <summary>Takes the snapshot's device list: refreshes availability, labels and the picker.</summary>
    public void ShowDevices(AudioDeviceList devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Equals(_devices))
        {
            return;
        }

        _devices = devices;
        RefreshSources();
        Picker.Refresh();
        MetersMayChange();
    }

    /// <summary>Takes the snapshot's meter levels; watches again when the engine is a new run or the link was lost.</summary>
    public void ShowMeters(MeterLevels levels, string engineInstanceId)
    {
        ArgumentNullException.ThrowIfNull(levels);
        _meters.Show(levels, engineInstanceId);
        ShowLevels();
    }

    /// <summary>The draft as Contracts' <see cref="TrackLayout"/>, as Save changes sends it.</summary>
    public TrackLayout ToLayout() => _draft.ToLayout();

    /// <summary>Adds <paramref name="device"/> to <paramref name="track"/>, reusing the draft's record of it.</summary>
    public void AddSound(AudioTrackCard track, AudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(device);
        if (_draft.AddSource(track.Model, device) is not null)
        {
            StructureChanged();
        }
    }

    /// <summary>Remove from this track: the device stays on its other Tracks, and leaves the saved tracks when it's on none.</summary>
    public void RemoveSound(AudioTrackCard track, AudioSourceRow source)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(source);
        _draft.RemoveSource(track.Model, source.Key);
        StructureChanged();
    }

    /// <summary>Treat as a microphone: one record, so it changes on every Track with this sound.</summary>
    public void SetMicrophone(AudioSourceRow source, bool isMic)
    {
        ArgumentNullException.ThrowIfNull(source);
        _draft.SetMic(source.Key, isMic);
        StructureChanged();
    }

    /// <summary>Moves a Track up (-1) or down (+1); its id is kept.</summary>
    public void MoveTrack(AudioTrackCard track, int by)
    {
        ArgumentNullException.ThrowIfNull(track);
        var from = _draft.Tracks.IndexOf(track.Model);
        var to = from + by;
        if (from < 0 || to < 0 || to >= _draft.Tracks.Count)
        {
            return;
        }

        _draft.Tracks.RemoveAt(from);
        _draft.Tracks.Insert(to, track.Model);
        StructureChanged();
    }

    /// <summary>Remove track: asks first when the Track has sounds.</summary>
    public void RemoveTrack(AudioTrackCard track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Sources.Count > 0 && !Confirm($"Remove \"{track.Name}\" and its {track.Summary}?", "Remove track"))
        {
            return;
        }

        if (Picker.Track == track)
        {
            Picker.Close();
        }

        _draft.Tracks.Remove(track.Model);
        StructureChanged();
    }

    protected override FrameworkElement CreateView()
    {
        var view = new TrackLayoutView { DataContext = this };
        if (IsProfile)
        {
            view.Unscroll();
        }

        return _view = view;
    }

    protected override void OnRender(SettingsDocument document)
    {
        var layout = SettingsValues.Resolve(document, Keys.TrackLayout, ProfileId);
        ShowUsesGlobal(IsProfile && !SettingsValues.HasOverride(document, Keys.TrackLayout, ProfileId!));
        if (UsesGlobal || !IsDirty)
        {
            _basis = layout;
            Load(layout);
        }
        else if (Same(layout, _basis) || Same(layout, ToLayout()))
        {
            // Another key changed, a save was refused, or this page's own save came back: keep the draft.
            _basis = layout;
            DraftChanged();
        }
        else
        {
            _basis = layout;
            Load(layout);
            Message = ChangedElsewhere;
        }
    }

    private void ShowUsesGlobal(bool value)
    {
        if (Set(ref _usesGlobal, value, nameof(UsesGlobal)))
        {
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(ShowsSummary));
        }
    }

    // Layouts compare as JSON, because records' lists compare by reference; a device on no Track doesn't count.
    private static bool Same(TrackLayout a, TrackLayout b) => JsonElement.DeepEquals(Normalized(a), Normalized(b));

    private static JsonElement Normalized(TrackLayout layout) => Keys.TrackLayout.ToJson(AudioTracksDraft.From(layout).ToLayout());

    private static string SummaryLine(AudioTrackCard track)
    {
        var sounds = track.Sources.Count == 0 ? "no sounds" : string.Join(", ", track.Sources.Select(source => source.Label));
        return $"{track.Number}  {track.Name}: {sounds}";
    }

    private void Load(TrackLayout layout)
    {
        // The picker's Track belongs to the draft being replaced.
        Picker.Close();
        _draft = AudioTracksDraft.From(layout);
        SaveProblem = null;
        Tracks.Clear();
        StructureChanged();
    }

    /// <summary>After any edit that changes the Tracks, their sources or a device record: syncs the cards, then tells the page.</summary>
    private void StructureChanged()
    {
        // Cards are kept by Track id, so a focused card or name box survives a move or an added sound.
        var cards = Tracks.ToDictionary(card => card.Id);
        for (var i = 0; i < _draft.Tracks.Count; i++)
        {
            var model = _draft.Tracks[i];
            if (!cards.Remove(model.Id, out var card) || card.Model != model)
            {
                card = new AudioTrackCard(this, model);
            }

            var at = Tracks.IndexOf(card);
            if (at < 0)
            {
                Tracks.Insert(i, card);
            }
            else if (at != i)
            {
                Tracks.Move(at, i);
            }

            card.Number = i + 1;
            card.ShowSources(_draft.SourcesOf(model));
        }

        while (Tracks.Count > _draft.Tracks.Count)
        {
            Tracks.RemoveAt(Tracks.Count - 1);
        }

        foreach (var card in Tracks)
        {
            card.IsFirst = card.Number == 1;
            card.IsLast = card.Number == Tracks.Count;
        }

        OnPropertyChanged(nameof(HasNoTracks));
        RefreshSources();
        Picker.Refresh();
        ShowLevels();
        DraftChanged();
    }

    /// <summary>After any draft edit: each Track's double-count line, the summary, whether the draft is dirty, and the watch.</summary>
    internal void DraftChanged()
    {
        if (SaveProblem == NameNeeded && Tracks.All(track => !string.IsNullOrWhiteSpace(track.Name)))
        {
            SaveProblem = null;
        }

        foreach (var card in Tracks)
        {
            // Outputs from two or more parent devices may carry the same audio twice. Inputs never count.
            var parents = card.Sources
                .Where(source => source.Device.Flow == AudioFlow.Output)
                .Select(source => source.Device.ParentName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            card.Warning = parents.Count >= 2
                ? $"You may hear some sounds twice: {JoinAnd(parents)} can both carry the same audio."
                : "";
            card.ShowNameProblem(SaveProblem == NameNeeded);
        }

        IsDirty = !Same(ToLayout(), _basis);
        OnPropertyChanged(nameof(Summary));
        MetersMayChange();
    }

    private static string JoinAnd(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    /// <summary>Re-watches when the metered layout (names aside) or the picker's probes changed.</summary>
    internal void MetersMayChange() => _meters.LayoutMayHaveChanged();

    /// <summary>Each Track's, source's and picker row's level from the last levels; 0 when closed or not listed.</summary>
    internal void ShowLevels()
    {
        var levels = _meters.Levels;
        foreach (var card in Tracks)
        {
            card.ShowPeak(levels.Tracks.FirstOrDefault(level => level.TrackId == card.Id)?.Peak ?? 0);
            foreach (var source in card.Sources)
            {
                source.ShowPeak(MeterProbes.LevelOf(levels, source.Device.EndpointId, source.Device.Flow));
            }
        }

        foreach (var row in Picker.Rows)
        {
            row.ShowPeak(MeterProbes.LevelOf(levels, row.Device.EndpointId, row.Device.Flow));
        }
    }

    /// <summary>The layout the meters watch: the draft, plus a probe per source and per listed device while the picker is open.</summary>
    private TrackLayout Watched() =>
        MeterProbes.WithProbes(ToLayout(), Picker.IsOpen ? _devices.Devices : null);

    /// <summary>Every distinct device the page knows, so a name two devices share shows its parent.</summary>
    internal Func<string, bool> SharedNames()
    {
        var held = Tracks.SelectMany(track => track.Sources).Select(source => source.Device);
        var known = _devices.Devices.Select(device => (device.EndpointId, device.Flow, device.FriendlyName))
            .Concat(held.Select(device => (device.EndpointId, device.Flow, device.FriendlyName)))
            .DistinctBy(device => (device.EndpointId.ToUpperInvariant(), device.Flow))
            .ToList();
        return name => known.Count(device => string.Equals(device.FriendlyName, name, StringComparison.OrdinalIgnoreCase)) > 1;
    }

    internal static string Label(string name, string parent, bool shared) =>
        shared && parent.Length > 0 ? $"{name} ({parent})" : name;

    private void RefreshSources()
    {
        var shared = SharedNames();
        foreach (var source in Tracks.SelectMany(track => track.Sources))
        {
            var device = source.Device;
            source.Label = Label(device.FriendlyName, device.ParentName, shared(device.FriendlyName));
            source.Available = _devices.Devices.Any(listed => listed.Available && listed.Flow == device.Flow
                && string.Equals(listed.EndpointId, device.EndpointId, StringComparison.OrdinalIgnoreCase));
        }

        OnPropertyChanged(nameof(Summary));
    }

    private void AddTrack()
    {
        var model = _draft.AddTrack($"Track {_draft.Tracks.Count + 1}");
        StructureChanged();
        Tracks.First(track => track.Model == model).RequestRename();
    }

    private void ApplyPreset(TrackLayoutPreset preset)
    {
        if (!CanEdit)
        {
            return;
        }

        var name = Presets.First(choice => choice.Preset == preset).Name;
        if (!Confirm($"Replace your audio tracks with {name}?", "Use a preset"))
        {
            return;
        }

        Picker.Close();
        _draft.ApplyPreset(preset, _devices.Devices);
        Tracks.Clear();
        StructureChanged();
    }

    private async Task SaveAsync()
    {
        // The draft stays dirty until settings.changed arrives, so a second Save would send against the old revision.
        if (_saving || !CanEdit)
        {
            return;
        }

        if (Tracks.Any(track => string.IsNullOrWhiteSpace(track.Name)))
        {
            SaveProblem = NameNeeded;
            DraftChanged();
            return;
        }

        SaveProblem = null;
        _saving = true;
        try
        {
            await SendAsync([SettingsValues.Set(Keys.TrackLayout, ToLayout(), ProfileId)]);
        }
        finally
        {
            _saving = false;
        }
    }

    private void Discard()
    {
        Message = null;
        Load(_basis);
    }
}
