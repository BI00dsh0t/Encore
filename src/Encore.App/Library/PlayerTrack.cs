namespace Encore.App.Library;

/// <summary>One of the file's Tracks in the player's list: its index and name; one Windows can't decode can't be chosen.</summary>
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
