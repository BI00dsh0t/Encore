namespace ReplayBuffer.App.Library;

/// <summary>
/// One of the file's Tracks in the player's Track list (story 14.4): its index in file order and its name, the
/// stream's <c>handler_name</c>, which our writers set to the Track's name (AD-9). A Track with no name is
/// "track n", as epic 10's glossary counts them from 1. A Track Windows can't decode (story 14.6) is shown as
/// unavailable and can't be chosen.
/// </summary>
internal sealed class PlayerTrack(int index, string? name, bool isAvailable = true) : Observable
{
    private bool _isPlaying;

    /// <summary>The Track's index in file order.</summary>
    public int Index { get; } = index;

    /// <summary>Whether Windows can decode it.</summary>
    public bool IsAvailable { get; } = isAvailable;

    /// <summary>The Track's name, or "track n" with none; "(unavailable)" after it when Windows can't decode it.</summary>
    public string Name { get; } = (string.IsNullOrWhiteSpace(name) ? $"track {index + 1}" : name.Trim()) + (isAvailable ? "" : " (unavailable)");

    /// <summary>Whether this is the Track that plays.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    public override string ToString() => Name;
}
