using System.Windows.Input;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>A sound on a Track: one device record, shown with its own level.</summary>
internal sealed class AudioSourceRow : Observable
{
    private readonly TrackLayoutViewModel _viewModel;
    private LayoutDevice _device;
    private string _label;
    private bool _available = true;
    private double _level;

    public AudioSourceRow(TrackLayoutViewModel viewModel, AudioTrackCard track, LayoutDevice device)
    {
        _viewModel = viewModel;
        _device = device;
        _label = device.FriendlyName;
        RemoveCommand = new RelayCommand(() => viewModel.RemoveSound(track, this));
    }

    public Guid Key => Device.Key;

    public LayoutDevice Device
    {
        get => _device;
        set
        {
            if (Set(ref _device, value))
            {
                OnPropertyChanged(nameof(IsMic));
                OnPropertyChanged(nameof(Kind));
                OnPropertyChanged(nameof(CanBeMic));
            }
        }
    }

    /// <summary>The name as Windows shows it, with the parent in brackets when two share a name.</summary>
    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public bool IsPlayback => Device.Flow == AudioFlow.Output;

    /// <summary>"playback", "microphone", or "input" for a recording device that isn't a microphone.</summary>
    public string Kind => AudioLevels.Kind(Device.Flow, Device.IsMic);

    /// <summary>Only a recording device offers Treat as a microphone; a playback device marked by hand can still be unmarked.</summary>
    public bool CanBeMic => Device.Flow == AudioFlow.Input || Device.IsMic;

    /// <summary>Treat as a microphone: on every Track with this sound.</summary>
    public bool IsMic
    {
        get => Device.IsMic;
        set
        {
            if (value != Device.IsMic)
            {
                _viewModel.SetMicrophone(this, value);
            }
        }
    }

    /// <summary>Whether the device list has it, present and active.</summary>
    public bool Available
    {
        get => _available;
        set
        {
            if (Set(ref _available, value))
            {
                OnPropertyChanged(nameof(Note));
                OnPropertyChanged(nameof(HasNote));
            }
        }
    }

    /// <summary>The not-connected note, or empty. It stays on the Track and records silence.</summary>
    public string Note => Available ? "" : "Not connected — records silence until it's back";

    public bool HasNote => !Available;

    public double Level
    {
        get => _level;
        private set => Set(ref _level, value);
    }

    public ICommand RemoveCommand { get; }

    public void ShowPeak(float peak) => Level = AudioLevels.Bar(peak);
}
