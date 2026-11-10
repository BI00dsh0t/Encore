using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Encore.App.Engine;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>Audio Tracks: the Global layout, or one game's, edited as one card per Track, with live meters. UI thread.</summary>
internal sealed class TrackLayoutViewModel : SettingsViewModel
{
    public const string PageTitle = "Audio Tracks";
    public const string NameNeeded = "Give every track a name to save.";
    public const string ChangedElsewhere = "Audio tracks were changed somewhere else, so this page shows the new ones.";

    private readonly IEngineSession _engine;
    private AudioDeviceList _devices = AudioDeviceList.Empty;
    private TrackLayout _basis = Keys.TrackLayout.Default;
    private AudioTracksDraft _draft = AudioTracksDraft.From(Keys.TrackLayout.Default);
    private bool _isDirty;
    private bool _saving;
    private bool _isOpen;
    private MeterLevels _levels = MeterLevels.Empty;
    private string? _engineInstanceId;
    private string? _watchedJson;
    private string? _watchedInstanceId;
    private bool _unwatchPending;
    private bool _usesGlobal;
    private string? _saveProblem;
    private AudioTrackCard? _pickerTrack;
    private string _pickerSearch = "";
    private AudioPickerRow? _pickerSelection;
    private FrameworkElement? _view;

    public TrackLayoutViewModel(SettingsSender sender, IEngineSession engine, string? profileId = null)
        : base(sender)
    {
        _engine = engine;
        ProfileId = profileId;
        _usesGlobal = profileId is not null;
        AddTrackCommand = new RelayCommand(AddTrack);
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        DiscardCommand = new RelayCommand(Discard);
        ApplyPresetCommand = new ParameterCommand(p => ApplyPreset(p is TrackLayoutPreset preset ? preset : TrackLayoutPreset.Simple));
        ClosePickerCommand = new RelayCommand(ClosePicker);
        Confirm = (message, caption) => Prompts.YesNo(_view, message, caption, MessageBoxImage.Question);
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
                _ = SendAsync([SettingsOp.RemoveProfile(ProfileId!, Keys.TrackLayout.Name)]);
            }
            else
            {
                _ = SendAsync([SettingsValues.Set(Keys.TrackLayout, global, ProfileId)]);
            }
        }
    }

    /// <summary>Whether the draft can be edited: always on the main page, else while the game has its own tracks.</summary>
    public bool CanEdit => !IsProfile || !UsesGlobal;

    /// <summary>The read-only summary mode: a game using the main tracks shows them as text.</summary>
    public bool ShowsSummary => !CanEdit;

    /// <summary>The Tracks and their sounds as text, one line per Track: "1  Everything: Speakers, Microphone".</summary>
    public IReadOnlyList<string> Summary => [.. Tracks.Select(t => $"{t.Number}  {t.Name}: {(t.Sources.Count == 0 ? "no sounds" : string.Join(", ", t.Sources.Select(s => s.Label)))}")];

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
                _unwatchPending = false;
                Watch();
            }
            else
            {
                ClosePicker();
                _watchedJson = null;
                _watchedInstanceId = null;
                _ = UnwatchAsync();
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

    public ICommand ClosePickerCommand { get; }

    /// <summary>The presets, with their menu labels.</summary>
    public IReadOnlyList<AudioPresetChoice> Presets { get; } =
    [
        new(TrackLayoutPreset.Simple, "Simple", "Simple — one track with your PC's sound and your microphone"),
        new(TrackLayoutPreset.ForEditing, "For editing", "For editing — everything, game and chat, and your microphone on their own tracks"),
    ];

    /// <summary>The Track the sound picker adds to, while it's open; null when it's closed.</summary>
    public AudioTrackCard? PickerTrack
    {
        get => _pickerTrack;
        private set
        {
            if (Set(ref _pickerTrack, value))
            {
                OnPropertyChanged(nameof(IsPickerOpen));
            }
        }
    }

    public bool IsPickerOpen => PickerTrack is not null;

    /// <summary>The picker's search text: rows whose name or parent contain it.</summary>
    public string PickerSearch
    {
        get => _pickerSearch;
        set
        {
            if (Set(ref _pickerSearch, value ?? ""))
            {
                RefreshPicker();
            }
        }
    }

    /// <summary>The picker's rows, playback first, then microphones and other inputs; the view groups them by section.</summary>
    public ObservableCollection<AudioPickerRow> PickerRows { get; } = [];

    /// <summary>The picker's highlighted row, kept across live device updates.</summary>
    public AudioPickerRow? PickerSelection
    {
        get => _pickerSelection;
        set => Set(ref _pickerSelection, value);
    }

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
        RefreshPicker();
        MetersMayChange();
    }

    /// <summary>Takes the snapshot's meter levels; watches again when the engine is a new run or the link was lost.</summary>
    public void ShowMeters(MeterLevels levels, string engineInstanceId)
    {
        ArgumentNullException.ThrowIfNull(levels);
        _levels = levels;
        _engineInstanceId = engineInstanceId;

        // The engine stops a watch when its last client leaves, so a reconnect watches again.
        if (_engine.Status != EngineStatus.Connected)
        {
            _watchedInstanceId = null;
            _watchedJson = null;
        }
        else if (IsOpen && _watchedInstanceId != engineInstanceId)
        {
            Watch();
        }
        else if (!IsOpen && _unwatchPending)
        {
            // A reconnect to the same engine run: its watch outlived the link, so stop it now.
            _unwatchPending = false;
            _ = UnwatchAsync();
        }

        ShowLevels();
    }

    /// <summary>The draft as Contracts' <see cref="TrackLayout"/>, as Save changes sends it.</summary>
    public TrackLayout ToLayout() => _draft.ToLayout();

    /// <summary>Opens the sound picker for <paramref name="track"/>; its rows meter every listed device.</summary>
    public void OpenPicker(AudioTrackCard track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!CanEdit)
        {
            return;
        }

        _pickerSearch = "";
        OnPropertyChanged(nameof(PickerSearch));
        PickerTrack = track;
        RefreshPicker();
        PickerSelection = PickerRows.FirstOrDefault(r => r.CanAdd);
        MetersMayChange();
    }

    public void ClosePicker()
    {
        if (PickerTrack is null)
        {
            return;
        }

        PickerTrack = null;
        PickerRows.Clear();
        PickerSelection = null;
        MetersMayChange();
    }

    /// <summary>Adds a picker row's device to the picker's Track, then closes the picker unless keepOpen (Ctrl+click).</summary>
    public void Pick(AudioPickerRow row, bool keepOpen = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (PickerTrack is not { } track || !row.CanAdd)
        {
            return;
        }

        AddSound(track, row.Device);
        if (keepOpen)
        {
            RefreshPicker();
            PickerSelection = PickerRows.FirstOrDefault(r => SameEndpoint(r.Device, row.Device));
        }
        else
        {
            ClosePicker();
        }
    }

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

        if (PickerTrack == track)
        {
            ClosePicker();
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
    private static bool Same(TrackLayout a, TrackLayout b) =>
        JsonElement.DeepEquals(Keys.TrackLayout.ToJson(AudioTracksDraft.From(a).ToLayout()), Keys.TrackLayout.ToJson(AudioTracksDraft.From(b).ToLayout()));

    private static bool SameEndpoint(AudioDevice a, AudioDevice b) =>
        a.Flow == b.Flow && string.Equals(a.EndpointId, b.EndpointId, StringComparison.OrdinalIgnoreCase);

    private void Load(TrackLayout layout)
    {
        // The picker's Track belongs to the draft being replaced.
        ClosePicker();
        _draft = AudioTracksDraft.From(layout);
        SaveProblem = null;
        Tracks.Clear();
        StructureChanged();
    }

    /// <summary>After any edit that changes the Tracks, their sources or a device record: syncs the cards, then tells the page.</summary>
    private void StructureChanged()
    {
        // Cards are kept by Track id, so a focused card or name box survives a move or an added sound.
        var cards = Tracks.ToDictionary(c => c.Id);
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
        RefreshPicker();
        ShowLevels();
        DraftChanged();
    }

    /// <summary>After any draft edit: each Track's double-count line, the summary, whether the draft is dirty, and the watch.</summary>
    internal void DraftChanged()
    {
        if (SaveProblem == NameNeeded && Tracks.All(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            SaveProblem = null;
        }

        foreach (var card in Tracks)
        {
            // Outputs from two or more parent devices may carry the same audio twice. Inputs never count.
            var parents = card.Sources
                .Where(s => s.Device.Flow == AudioFlow.Output)
                .Select(s => s.Device.ParentName)
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
    private void MetersMayChange()
    {
        if (IsOpen && MeteredJson() != _watchedJson)
        {
            Watch();
        }
    }

    /// <summary>The layout the meters watch: the draft, plus a probe per source and per listed device while the picker is open.</summary>
    private TrackLayout Watched() =>
        MeterProbes.WithProbes(ToLayout(), IsPickerOpen ? _devices.Devices : null);

    // Names don't change what the engine meters, and the name box updates per keystroke.
    private string MeteredJson()
    {
        var layout = Watched();
        return Keys.TrackLayout.ToJson(layout with { Tracks = [.. layout.Tracks.Select(t => t with { Name = "" })] }).GetRawText();
    }

    private void Watch()
    {
        _watchedJson = MeteredJson();
        _watchedInstanceId = _engineInstanceId;
        _ = WatchAsync(_watchedJson, new MetersWatch(Watched()));
    }

    private async Task WatchAsync(string json, MetersWatch watch)
    {
        try
        {
            await _engine.SendAsync(AudioMessages.Watch, watch);
        }
        catch (ErrorCodeException e)
        {
            // Refused: the next draft change tries again. Disconnected: so does the next snapshot.
            if (_watchedJson == json)
            {
                _watchedJson = null;
                if (e.Code == ErrorCodes.EngineDisconnected)
                {
                    _watchedInstanceId = null;
                }
            }
        }
    }

    private async Task UnwatchAsync()
    {
        try
        {
            await _engine.SendAsync(AudioMessages.Unwatch, Empty.Value);
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.EngineDisconnected)
        {
            // The engine may still be metering; the next snapshot sends this again while the page stays closed.
            _unwatchPending = !IsOpen;
        }
        catch (ErrorCodeException)
        {
            // Refused: there is nothing to stop.
        }
    }

    /// <summary>Each Track's, source's and picker row's level from the last levels; 0 when closed or not listed.</summary>
    private void ShowLevels()
    {
        var levels = IsOpen ? _levels : MeterLevels.Empty;
        foreach (var card in Tracks)
        {
            card.ShowPeak(levels.Tracks.FirstOrDefault(l => l.TrackId == card.Id)?.Peak ?? 0);
            foreach (var source in card.Sources)
            {
                source.ShowPeak(MeterProbes.LevelOf(levels, source.Device.EndpointId, source.Device.Flow));
            }
        }

        foreach (var row in PickerRows)
        {
            row.ShowPeak(MeterProbes.LevelOf(levels, row.Device.EndpointId, row.Device.Flow));
        }
    }

    /// <summary>Every distinct device the page knows, so a name two devices share shows its parent.</summary>
    private Func<string, bool> SharedNames()
    {
        var known = _devices.Devices.Select(d => (d.EndpointId, d.Flow, d.FriendlyName))
            .Concat(Tracks.SelectMany(t => t.Sources).Select(s => (s.Device.EndpointId, s.Device.Flow, s.Device.FriendlyName)))
            .DistinctBy(d => (d.EndpointId.ToUpperInvariant(), d.Flow))
            .ToList();
        return name => known.Count(d => string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase)) > 1;
    }

    internal static string Label(string name, string parent, bool shared) =>
        shared && parent.Length > 0 ? $"{name} ({parent})" : name;

    private void RefreshSources()
    {
        var shared = SharedNames();
        foreach (var source in Tracks.SelectMany(t => t.Sources))
        {
            var device = source.Device;
            source.Label = Label(device.FriendlyName, device.ParentName, shared(device.FriendlyName));
            source.Available = _devices.Devices.Any(d => d.Available && d.Flow == device.Flow
                && string.Equals(d.EndpointId, device.EndpointId, StringComparison.OrdinalIgnoreCase));
        }

        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Rebuilds the picker's rows from the device list, keeping the highlighted row.</summary>
    private void RefreshPicker()
    {
        if (PickerTrack is not { } track)
        {
            return;
        }

        var selected = PickerSelection?.Device;
        var shared = SharedNames();
        var held = track.Sources.Select(s => s.Device).ToList();
        PickerRows.Clear();
        foreach (var device in _devices.Devices.OrderBy(d => d.Flow))
        {
            var label = Label(device.FriendlyName, device.ParentName, shared(device.FriendlyName));
            if (PickerSearch.Length > 0
                && !label.Contains(PickerSearch, StringComparison.OrdinalIgnoreCase)
                && !device.ParentName.Contains(PickerSearch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var added = held.Any(h => h.Flow == device.Flow && string.Equals(h.EndpointId, device.EndpointId, StringComparison.OrdinalIgnoreCase));
            PickerRows.Add(new AudioPickerRow(device, label, added));
        }

        PickerSelection = selected is null ? null : PickerRows.FirstOrDefault(r => SameEndpoint(r.Device, selected));
        ShowLevels();
    }

    private void AddTrack()
    {
        var model = _draft.AddTrack($"Track {_draft.Tracks.Count + 1}");
        StructureChanged();
        Tracks.First(t => t.Model == model).RequestRename();
    }

    private void ApplyPreset(TrackLayoutPreset preset)
    {
        if (!CanEdit)
        {
            return;
        }

        var name = Presets.First(p => p.Preset == preset).Name;
        if (!Confirm($"Replace your audio tracks with {name}?", "Use a preset"))
        {
            return;
        }

        ClosePicker();
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

        if (Tracks.Any(t => string.IsNullOrWhiteSpace(t.Name)))
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
