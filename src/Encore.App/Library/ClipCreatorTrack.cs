using Encore.Contracts.Messages;

namespace Encore.App.Library;

internal sealed class ClipCreatorTrack : Observable
{
    private bool _included = true;

    public ClipCreatorTrack(ClipAudioTrack track)
    {
        Track = track;
    }

    public ClipAudioTrack Track { get; }

    public string Name => Track.Name;

    public bool Included
    {
        get => _included;
        set
        {
            if (Set(ref _included, value))
            {
                Changed?.Invoke();
            }
        }
    }

    public event Action? Changed;
}
