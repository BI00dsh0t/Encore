using System.Collections.ObjectModel;
using System.Windows.Input;
using Encore.Contracts.Messages;

namespace Encore.App.Settings;

/// <summary>The sound picker of Audio Tracks: the devices one Track can add, searched and metered while it is open.</summary>
internal sealed class SoundPicker : Observable
{
    private readonly TrackLayoutViewModel _layout;
    private AudioTrackCard? _track;
    private string _search = "";
    private AudioPickerRow? _selection;

    public SoundPicker(TrackLayoutViewModel layout)
    {
        _layout = layout;
        CloseCommand = new RelayCommand(Close);
    }

    /// <summary>The Track the picker adds to, while it's open; null when it's closed.</summary>
    public AudioTrackCard? Track
    {
        get => _track;
        private set
        {
            if (Set(ref _track, value))
            {
                OnPropertyChanged(nameof(IsOpen));
            }
        }
    }

    public bool IsOpen => Track is not null;

    /// <summary>The search text: rows whose name or parent contain it.</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value ?? ""))
            {
                Refresh();
            }
        }
    }

    /// <summary>The rows, playback first, then microphones and other inputs; the view groups them by section.</summary>
    public ObservableCollection<AudioPickerRow> Rows { get; } = [];

    /// <summary>The highlighted row, kept across live device updates.</summary>
    public AudioPickerRow? Selection
    {
        get => _selection;
        set => Set(ref _selection, value);
    }

    public ICommand CloseCommand { get; }

    /// <summary>Opens the picker for <paramref name="track"/>; its rows meter every listed device.</summary>
    public void Open(AudioTrackCard track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!_layout.CanEdit)
        {
            return;
        }

        _search = "";
        OnPropertyChanged(nameof(Search));
        Track = track;
        Refresh();
        Selection = Rows.FirstOrDefault(row => row.CanAdd);
        _layout.MetersMayChange();
    }

    public void Close()
    {
        if (Track is null)
        {
            return;
        }

        Track = null;
        Rows.Clear();
        Selection = null;
        _layout.MetersMayChange();
    }

    /// <summary>Adds a row's device to the picker's Track, then closes the picker unless keepOpen (Ctrl+click).</summary>
    public void Pick(AudioPickerRow row, bool keepOpen = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Track is not { } track || !row.CanAdd)
        {
            return;
        }

        _layout.AddSound(track, row.Device);
        if (keepOpen)
        {
            Refresh();
            Selection = Rows.FirstOrDefault(other => SameEndpoint(other.Device, row.Device));
        }
        else
        {
            Close();
        }
    }

    /// <summary>Rebuilds the rows from the device list, keeping the highlighted row.</summary>
    public void Refresh()
    {
        if (Track is not { } track)
        {
            return;
        }

        var selected = Selection?.Device;
        var shared = _layout.SharedNames();
        var held = track.Sources.Select(source => source.Device).ToList();
        Rows.Clear();
        foreach (var device in _layout.Devices.Devices.OrderBy(device => device.Flow))
        {
            var label = TrackLayoutViewModel.Label(device.FriendlyName, device.ParentName, shared(device.FriendlyName));
            if (!Matches(label, device))
            {
                continue;
            }

            var added = held.Any(layoutDevice => layoutDevice.Flow == device.Flow
                && string.Equals(layoutDevice.EndpointId, device.EndpointId, StringComparison.OrdinalIgnoreCase));
            Rows.Add(new AudioPickerRow(device, label, added));
        }

        Selection = selected is null ? null : Rows.FirstOrDefault(row => SameEndpoint(row.Device, selected));
        _layout.ShowLevels();
    }

    private static bool SameEndpoint(AudioDevice a, AudioDevice b) =>
        a.Flow == b.Flow && string.Equals(a.EndpointId, b.EndpointId, StringComparison.OrdinalIgnoreCase);

    private bool Matches(string label, AudioDevice device) =>
        Search.Length == 0
        || label.Contains(Search, StringComparison.OrdinalIgnoreCase)
        || device.ParentName.Contains(Search, StringComparison.OrdinalIgnoreCase);
}
