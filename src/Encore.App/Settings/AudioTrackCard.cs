using System.Collections.ObjectModel;
using System.Windows.Input;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>A Track's card. <see cref="Id"/> stays the same across renames and moves.</summary>
internal sealed class AudioTrackCard : Observable
{
    private readonly TrackLayoutViewModel _viewModel;
    private int _number;
    private string _warning = "";
    private double _level;
    private bool _nameProblem;
    private bool _isFirst;
    private bool _isLast;
    private bool _pendingRename;

    public AudioTrackCard(TrackLayoutViewModel viewModel, AudioTracksDraftTrack model)
    {
        _viewModel = viewModel;
        Model = model;
        RenameCommand = new RelayCommand(RequestRename);
        MoveUpCommand = new RelayCommand(() => viewModel.MoveTrack(this, -1));
        MoveDownCommand = new RelayCommand(() => viewModel.MoveTrack(this, 1));
        RemoveCommand = new RelayCommand(() => viewModel.RemoveTrack(this));
        AddSoundCommand = new RelayCommand(() => viewModel.Picker.Open(this));
    }

    /// <summary>Raised when the card's name box should take focus (Rename, F2, a new Track).</summary>
    public event Action? RenameRequested;

    internal AudioTracksDraftTrack Model { get; }

    public Guid Id => Model.Id;

    /// <summary>The track number, 1-based, as players and editors count them.</summary>
    public int Number
    {
        get => _number;
        set => Set(ref _number, value);
    }

    public string Name
    {
        get => Model.Name;
        set
        {
            if (value == Model.Name)
            {
                return;
            }

            Model.Name = value ?? "";
            OnPropertyChanged();
            _viewModel.DraftChanged();
        }
    }

    /// <summary>The mute switch: whether the Mic mute key silences this Track's microphones.</summary>
    public bool MuteAffected
    {
        get => Model.MuteAffected;
        set
        {
            if (value == Model.MuteAffected)
            {
                return;
            }

            Model.MuteAffected = value;
            OnPropertyChanged();
            _viewModel.DraftChanged();
        }
    }

    /// <summary>The sounds this Track records, in order.</summary>
    public ObservableCollection<AudioSourceRow> Sources { get; } = [];

    /// <summary>"{n} sounds", or the silent line.</summary>
    public string Summary => Sources.Count switch
    {
        0 => "No sounds — this track will be silent",
        1 => "1 sound",
        var n => $"{n} sounds",
    };

    public bool IsSilent => Sources.Count == 0;

    /// <summary>The mute switch shows only when the Track has a microphone.</summary>
    public bool HasMicrophone => Sources.Any(source => source.IsMic);

    /// <summary>The double-count line; empty when there is none. It never blocks Save.</summary>
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

    /// <summary>Whether the name box is outlined: a save was refused for a blank name, and this one is blank.</summary>
    public bool HasNameProblem
    {
        get => _nameProblem;
        private set => Set(ref _nameProblem, value);
    }

    public bool IsFirst
    {
        get => _isFirst;
        set => Set(ref _isFirst, value);
    }

    public bool IsLast
    {
        get => _isLast;
        set => Set(ref _isLast, value);
    }

    /// <summary>The Track's level bar, 0..1: its linear peak on a −60..0 dBFS scale.</summary>
    public double Level
    {
        get => _level;
        private set => Set(ref _level, value);
    }

    public ICommand RenameCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand AddSoundCommand { get; }

    /// <summary>Sets <see cref="Level"/> from a linear sample peak.</summary>
    public void ShowPeak(float peak) => Level = AudioLevels.Bar(peak);

    /// <summary>Focuses the card's name box; a card whose view isn't loaded yet takes it when it loads.</summary>
    public void RequestRename()
    {
        if (RenameRequested is { } handler)
        {
            handler();
        }
        else
        {
            _pendingRename = true;
        }
    }

    /// <summary>Whether a rename was asked for before the view loaded; clears it.</summary>
    public bool TakePendingRename()
    {
        var pending = _pendingRename;
        _pendingRename = false;
        return pending;
    }

    internal void ShowNameProblem(bool refused) => HasNameProblem = refused && string.IsNullOrWhiteSpace(Name);

    internal void ShowSources(IReadOnlyList<LayoutDevice> devices)
    {
        var rows = Sources.ToDictionary(source => source.Key);
        Sources.Clear();
        foreach (var device in devices)
        {
            if (rows.TryGetValue(device.Key, out var row))
            {
                row.Device = device;
            }
            else
            {
                row = new AudioSourceRow(_viewModel, this, device);
            }

            Sources.Add(row);
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsSilent));
        OnPropertyChanged(nameof(HasMicrophone));
    }
}
