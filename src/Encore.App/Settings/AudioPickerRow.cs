using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>A device the sound picker lists.</summary>
internal sealed class AudioPickerRow(AudioDevice device, string label, bool added) : Observable
{
    private double _level;

    public AudioDevice Device { get; } = device;

    public string Label { get; } = label;

    /// <summary>The section header the view groups by.</summary>
    public string Section => Device.Flow == AudioFlow.Output ? "Sounds from your PC (playback)" : "Microphones and other inputs";

    public bool IsPlayback => Device.Flow == AudioFlow.Output;

    /// <summary>"Windows default" for the default of its section, else the parent device's name.</summary>
    public string Caption => Device.IsDefault ? "Windows default" : Device.ParentName;

    public bool IsDefault => Device.IsDefault;

    /// <summary>The note for a device the layout holds that isn't connected.</summary>
    public string Note => Device.Available ? "" : "Not connected";

    /// <summary>Already on this Track: listed ticked and disabled.</summary>
    public bool IsAdded { get; } = added;

    public bool CanAdd => !IsAdded;

    public double Level
    {
        get => _level;
        private set => Set(ref _level, value);
    }

    public void ShowPeak(float peak) => Level = AudioLevels.Bar(peak);
}
