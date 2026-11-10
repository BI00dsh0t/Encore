using System.Windows.Input;
using Encore.Contracts.Messages;

namespace Encore.App.Settings;

/// <summary>One sound on the Feedback Sounds page: its switch and its Play button.</summary>
internal sealed class FeedbackSoundRow : Observable
{
    private readonly FeedbackSoundsViewModel _viewModel;

    public FeedbackSoundRow(FeedbackSoundsViewModel viewModel, FeedbackSound sound, string label)
    {
        _viewModel = viewModel;
        Sound = sound;
        Label = label;
        PlayCommand = new RelayCommand(() => _ = viewModel.PlayAsync(sound));
    }

    public FeedbackSound Sound { get; }

    public string Label { get; }

    /// <summary>The sound's <c>feedbackSounds</c> flag; setting it sends the whole value with that flag changed.</summary>
    public bool IsOn
    {
        get => _viewModel.IsOn(Sound);
        set => _viewModel.Switch(this, value);
    }

    /// <summary>Sends <c>feedback.preview</c> for the sound, whether or not it is switched on.</summary>
    public ICommand PlayCommand { get; }

    internal void Changed() => OnPropertyChanged(nameof(IsOn));
}
