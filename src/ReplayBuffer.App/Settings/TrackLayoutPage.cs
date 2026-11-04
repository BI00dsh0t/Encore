using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Settings;

/// <summary>
/// The Global <c>trackLayout</c> (R1, R2), or with a profile id that Game Profile's override of it (R7):
/// an unsaved draft of its devices and Tracks, which Save sends as
/// one <c>settings.update</c>. Devices come from the snapshot's <c>devices</c> list; a layout device that
/// isn't present shows as unavailable and stays in the layout. While the page is open it meters its draft
/// (R6) through <c>meters.watch</c>, and each Track shows its level from the snapshot's <c>meters</c>.
/// A profile without an override shows the Global layout, which it can't edit, until "Use Global" is
/// unticked. Members run on the UI thread.
/// </summary>
internal sealed class TrackLayoutPage : SettingsPage
{
    private readonly IEngineSession _engine;
    private AudioDeviceList _devices = AudioDeviceList.Empty;
    private TrackLayout _basis = Keys.TrackLayout.Default;
    private bool _isDirty;
    private bool _saving;
    private TrackLayoutDeviceItem? _selectedDevice;
    private TrackLayoutTrackItem? _selectedTrack;
    private TrackLayoutChoice? _selectedChoice;
    private bool _isOpen;
    private MeterLevels _levels = MeterLevels.Empty;
    private string? _engineInstanceId;
    private string? _watchedJson;
    private string? _watchedInstanceId;
    private bool _unwatchPending;
    private bool _usesGlobal;

    public TrackLayoutPage(SettingsSender sender, IEngineSession engine, string? profileId = null)
        : base(sender)
    {
        _engine = engine;
        ProfileId = profileId;
        _usesGlobal = profileId is not null;
        AddDeviceCommand = new RelayCommand(AddDevice);
        RemoveDeviceCommand = new RelayCommand(RemoveDevice);
        AddTrackCommand = new RelayCommand(AddTrack);
        MoveUpCommand = new RelayCommand(() => MoveTrack(-1));
        MoveDownCommand = new RelayCommand(() => MoveTrack(1));
        DeleteTrackCommand = new RelayCommand(DeleteTrack);
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        RevertCommand = new RelayCommand(Revert);
        Load(_basis);
    }

    public override string Title => "Track Layout";

    /// <summary>The Game Profile whose override this page edits; null on the Global page.</summary>
    public string? ProfileId { get; }

    public bool IsProfile => ProfileId is not null;

    /// <summary>
    /// Whether the profile has no override and so uses the Global layout; false on the Global page.
    /// Ticking it removes the override and drops the draft; unticking it sets the override to the Global
    /// layout as is, so the Track ids and device keys stay the same.
    /// </summary>
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

    /// <summary>Whether the draft can be edited: always on the Global page, else while the profile has its own layout.</summary>
    public bool CanEdit => !IsProfile || !UsesGlobal;

    /// <summary>The draft's devices, in layout order.</summary>
    public ObservableCollection<TrackLayoutDeviceItem> Devices { get; } = [];

    /// <summary>The draft's Tracks, in layout order.</summary>
    public ObservableCollection<TrackLayoutTrackItem> Tracks { get; } = [];

    /// <summary>The listed devices the draft doesn't hold yet.</summary>
    public ObservableCollection<TrackLayoutChoice> Choices { get; } = [];

    public TrackLayoutDeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set => Set(ref _selectedDevice, value);
    }

    public TrackLayoutTrackItem? SelectedTrack
    {
        get => _selectedTrack;
        set => Set(ref _selectedTrack, value);
    }

    public TrackLayoutChoice? SelectedChoice
    {
        get => _selectedChoice;
        set => Set(ref _selectedChoice, value);
    }

    /// <summary>Whether the draft differs from the layout the page rendered.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value);
    }

    /// <summary>
    /// Whether the page is on screen: the window shown and this settings page selected in the nav. Opening
    /// watches the draft; closing sends <c>meters.unwatch</c> and zeroes every level.
    /// </summary>
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
                _watchedJson = null;
                _watchedInstanceId = null;
                _ = UnwatchAsync();
            }

            ShowLevels();
        }
    }

    public ICommand AddDeviceCommand { get; }

    public ICommand RemoveDeviceCommand { get; }

    public ICommand AddTrackCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand DeleteTrackCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand RevertCommand { get; }

    /// <summary>Takes the snapshot's device list: refreshes availability and labels, and the picker's choices.</summary>
    public void ShowDevices(AudioDeviceList devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Equals(_devices))
        {
            return;
        }

        _devices = devices;
        RefreshDevices();
    }

    /// <summary>
    /// Takes the snapshot's meter levels and engine run: sets each Track's level, and watches the draft
    /// again when the page is open and the engine is a new run.
    /// </summary>
    public void ShowMeters(MeterLevels levels, string engineInstanceId)
    {
        ArgumentNullException.ThrowIfNull(levels);
        _levels = levels;
        _engineInstanceId = engineInstanceId;
        if (IsOpen && _watchedInstanceId != engineInstanceId)
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

    /// <summary>The draft as Contracts' <see cref="TrackLayout"/>.</summary>
    public TrackLayout ToLayout() => new(
        [.. Devices.Select(d => d.Device)],
        [.. Tracks.Select(t => new Track(t.Id, t.Name, [.. t.DeviceKeys], t.MuteAffected))]);

    protected override FrameworkElement CreateView()
    {
        var view = new TrackLayoutView { DataContext = this };
        return IsProfile ? Embedded(view) : view;
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
            Message = "The Track Layout changed elsewhere, so this page reloaded it.";
        }
    }

    private void ShowUsesGlobal(bool value)
    {
        if (Set(ref _usesGlobal, value, nameof(UsesGlobal)))
        {
            OnPropertyChanged(nameof(CanEdit));
        }
    }

    // Records' list members compare by reference, so layouts compare as JSON.
    private static bool Same(TrackLayout a, TrackLayout b) =>
        JsonElement.DeepEquals(Keys.TrackLayout.ToJson(a), Keys.TrackLayout.ToJson(b));

    private static bool SameEndpoint(string endpointId, AudioFlow flow, string otherId, AudioFlow otherFlow) =>
        flow == otherFlow && string.Equals(endpointId, otherId, StringComparison.OrdinalIgnoreCase);

    private void Load(TrackLayout layout)
    {
        var selectedTrackId = SelectedTrack?.Id;
        SelectedDevice = null;
        Devices.Clear();
        foreach (var device in layout.Devices)
        {
            Devices.Add(new TrackLayoutDeviceItem(device, DraftChanged));
        }

        Tracks.Clear();
        foreach (var track in layout.Tracks)
        {
            Tracks.Add(new TrackLayoutTrackItem(track, DraftChanged));
        }

        ShowLevels();
        SelectedTrack = Tracks.FirstOrDefault(t => t.Id == selectedTrackId) ?? Tracks.FirstOrDefault();
        DevicesChanged();
        DraftChanged();
    }

    /// <summary>After any draft edit: each Track's double-count warning, then whether the draft is dirty.</summary>
    private void DraftChanged()
    {
        foreach (var track in Tracks)
        {
            // R2: outputs from two or more parent devices may carry the same audio twice. Inputs never count.
            var parents = track.DeviceKeys
                .Select(key => Devices.FirstOrDefault(d => d.Device.Key == key)?.Device)
                .Where(d => d is { Flow: AudioFlow.Output })
                .Select(d => d!.ParentName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            track.Warning = parents.Count >= 2
                ? $"May count audio twice: mixes outputs from {string.Join(", ", parents)}."
                : "";
        }

        IsDirty = !Same(ToLayout(), _basis);
        if (IsOpen && MeteredJson() != _watchedJson)
        {
            Watch();
        }
    }

    // Names don't change what the engine meters, and the name box updates per keystroke.
    private string MeteredJson()
    {
        var layout = ToLayout();
        return Keys.TrackLayout.ToJson(layout with { Tracks = [.. layout.Tracks.Select(t => t with { Name = "" })] }).GetRawText();
    }

    private void Watch()
    {
        _watchedJson = MeteredJson();
        _watchedInstanceId = _engineInstanceId;
        _ = WatchAsync(_watchedJson, new MetersWatch(ToLayout()));
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

    /// <summary>Each Track's level from the last levels; 0 when the page is closed or the levels don't list it.</summary>
    private void ShowLevels()
    {
        foreach (var track in Tracks)
        {
            track.ShowPeak(IsOpen ? _levels.Tracks.FirstOrDefault(l => l.TrackId == track.Id)?.Peak ?? 0 : 0);
        }
    }

    /// <summary>After the draft's devices change: the Tracks' checkboxes, the labels and the choices.</summary>
    private void DevicesChanged()
    {
        foreach (var track in Tracks)
        {
            track.ShowDevices(Devices);
        }

        RefreshDevices();
    }

    private void RefreshDevices()
    {
        // Every distinct device the page knows, so a shared friendly name shows the parents.
        var known = _devices.Devices.Select(d => (d.EndpointId, d.Flow, d.FriendlyName))
            .Concat(Devices.Select(d => (d.Device.EndpointId, d.Device.Flow, d.Device.FriendlyName)))
            .DistinctBy(d => (d.EndpointId.ToUpperInvariant(), d.Flow))
            .ToList();
        bool Shared(string name) => known.Count(d => string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase)) > 1;
        string Label(string name, string parent, AudioFlow flow) =>
            (Shared(name) && parent.Length > 0 ? $"{name} — {parent}" : name) + (flow == AudioFlow.Output ? " (Output)" : " (Input)");

        foreach (var item in Devices)
        {
            var device = item.Device;
            item.Label = Label(device.FriendlyName, device.ParentName, device.Flow);
            item.Available = _devices.Devices.Any(d => d.Available && SameEndpoint(d.EndpointId, d.Flow, device.EndpointId, device.Flow));
        }

        var selected = SelectedChoice?.Device;
        Choices.Clear();
        foreach (var device in _devices.Devices)
        {
            if (!Devices.Any(d => SameEndpoint(d.Device.EndpointId, d.Device.Flow, device.EndpointId, device.Flow)))
            {
                Choices.Add(new TrackLayoutChoice(device, Label(device.FriendlyName, device.ParentName, device.Flow)));
            }
        }

        SelectedChoice = selected is null
            ? null
            : Choices.FirstOrDefault(c => SameEndpoint(c.Device.EndpointId, c.Device.Flow, selected.EndpointId, selected.Flow));
    }

    private void AddDevice()
    {
        if (SelectedChoice is not { } choice)
        {
            return;
        }

        var source = choice.Device;
        var device = new LayoutDevice(Guid.NewGuid(), source.EndpointId, source.FriendlyName, source.ParentName, source.Flow, IsMic: false);
        Devices.Add(new TrackLayoutDeviceItem(device, DraftChanged));
        Tracks.FirstOrDefault(t => t.Id == Keys.EverythingTrackId)?.DeviceKeys.Add(device.Key);
        SelectedChoice = null;
        DevicesChanged();
        DraftChanged();
    }

    private void RemoveDevice()
    {
        if (SelectedDevice is not { } item)
        {
            return;
        }

        Devices.Remove(item);
        foreach (var track in Tracks)
        {
            track.DeviceKeys.Remove(item.Device.Key);
        }

        SelectedDevice = null;
        DevicesChanged();
        DraftChanged();
    }

    private void AddTrack()
    {
        var track = new TrackLayoutTrackItem(new Track(Guid.NewGuid(), $"Track {Tracks.Count + 1}", [], MuteAffected: false), DraftChanged);
        track.ShowDevices(Devices);
        Tracks.Add(track);
        ShowLevels();
        SelectedTrack = track;
        DraftChanged();
    }

    private void MoveTrack(int by)
    {
        if (SelectedTrack is not { } track)
        {
            return;
        }

        var from = Tracks.IndexOf(track);
        var to = from + by;
        if (to < 0 || to >= Tracks.Count)
        {
            return;
        }

        Tracks.Move(from, to);
        SelectedTrack = track;
        DraftChanged();
    }

    private void DeleteTrack()
    {
        if (SelectedTrack is not { } track)
        {
            return;
        }

        var index = Tracks.IndexOf(track);
        Tracks.Remove(track);
        SelectedTrack = Tracks.Count == 0 ? null : Tracks[Math.Min(index, Tracks.Count - 1)];
        DraftChanged();
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
            Message = "Not saved: every Track needs a name.";
            return;
        }

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

    private void Revert()
    {
        Message = null;
        Load(_basis);
    }
}

/// <summary>A device of the draft layout.</summary>
internal sealed class TrackLayoutDeviceItem(LayoutDevice device, Action changed) : Observable
{
    private string _label = device.FriendlyName;
    private bool _available;

    /// <summary>The draft's device; marking it a Mic replaces it with the same key.</summary>
    public LayoutDevice Device { get; private set; } = device;

    /// <summary>Only an Input Device can be a Mic Device; an Output Device marked by hand can still be unmarked.</summary>
    public bool CanBeMic => Device.Flow == AudioFlow.Input || Device.IsMic;

    /// <summary>Whether the Mic Mute Hotkey mutes this device (R4).</summary>
    public bool IsMic
    {
        get => Device.IsMic;
        set
        {
            if (value == Device.IsMic)
            {
                return;
            }

            Device = Device with { IsMic = value };
            OnPropertyChanged();
            changed();
        }
    }

    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    /// <summary>Whether the snapshot's device list has this endpoint, present and active.</summary>
    public bool Available
    {
        get => _available;
        set
        {
            if (Set(ref _available, value))
            {
                OnPropertyChanged(nameof(Note));
            }
        }
    }

    /// <summary>"unavailable" when the device isn't present, else empty.</summary>
    public string Note => Available ? "" : "unavailable";
}

/// <summary>A Track of the draft layout. <see cref="Id"/> stays the same across renames and moves.</summary>
internal sealed class TrackLayoutTrackItem(Track track, Action changed) : Observable
{
    private string _name = track.Name;
    private bool _muteAffected = track.MuteAffected;
    private string _warning = "";
    private double _level;

    public Guid Id { get; } = track.Id;

    /// <summary>The Track's level bar, 0..1: its linear peak on a −60..0 dBFS scale.</summary>
    public double Level
    {
        get => _level;
        private set => Set(ref _level, value);
    }

    /// <summary>Sets <see cref="Level"/> from a linear sample peak.</summary>
    public void ShowPeak(float peak) =>
        Level = peak <= 0 ? 0 : Math.Clamp(((20 * Math.Log10(peak)) + 60) / 60, 0, 1);

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
            {
                changed();
            }
        }
    }

    /// <summary>Whether the Mic Mute Hotkey mutes the Mic Devices in this Track.</summary>
    public bool MuteAffected
    {
        get => _muteAffected;
        set
        {
            if (Set(ref _muteAffected, value))
            {
                changed();
            }
        }
    }

    /// <summary>The double-count warning; empty when there is none. It never blocks Save.</summary>
    public string Warning
    {
        get => _warning;
        set
        {
            if (Set(ref _warning, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => Warning.Length > 0;

    /// <summary>The Track's device keys, in order; a checked device is appended.</summary>
    public List<Guid> DeviceKeys { get; } = [.. track.DeviceKeys];

    /// <summary>One checkbox per layout device.</summary>
    public ObservableCollection<TrackLayoutCheck> Checks { get; } = [];

    public void ShowDevices(IEnumerable<TrackLayoutDeviceItem> devices)
    {
        Checks.Clear();
        foreach (var device in devices)
        {
            Checks.Add(new TrackLayoutCheck(this, device));
        }
    }

    internal void Assign(Guid key, bool assigned)
    {
        if (assigned == DeviceKeys.Contains(key))
        {
            return;
        }

        if (assigned)
        {
            DeviceKeys.Add(key);
        }
        else
        {
            DeviceKeys.Remove(key);
        }

        changed();
    }
}

/// <summary>Whether a Track holds one layout device.</summary>
internal sealed class TrackLayoutCheck(TrackLayoutTrackItem track, TrackLayoutDeviceItem device) : Observable
{
    public TrackLayoutDeviceItem Device { get; } = device;

    public bool IsChecked
    {
        get => track.DeviceKeys.Contains(Device.Device.Key);
        set
        {
            track.Assign(Device.Device.Key, value);
            OnPropertyChanged();
        }
    }
}

/// <summary>A listed device the picker offers.</summary>
internal sealed record TrackLayoutChoice(AudioDevice Device, string Label)
{
    /// <summary>"unavailable" when the listed device isn't present, else empty.</summary>
    public string Note => Device.Available ? "" : "unavailable";
}
