namespace ReplayBuffer.App.Library;

/// <summary>
/// One of the file's Tracks in the player's Track list (story 14.4): its index in file order and its name, the
/// stream's <c>handler_name</c>, which our writers set to the Track's name (AD-9). A Track with no name is
/// "track n", as epic 10's glossary counts them from 1.
/// </summary>
internal sealed class PlayerTrack(int index, string? name) : Observable
{
    private bool _isPlaying;

    /// <summary>The Track's index in file order.</summary>
    public int Index { get; } = index;

    /// <summary>The Track's name, or "track n" with none.</summary>
    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? $"track {index + 1}" : name.Trim();

    /// <summary>Whether this is the Track that plays.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    public override string ToString() => Name;
}
