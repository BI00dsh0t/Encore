namespace Encore.App.Library;

/// <summary>What an opened file holds: video size, length when known, Track names, and what Windows can decode.</summary>
internal sealed record PlayerMedia(
    int Width,
    int Height,
    TimeSpan? Duration,
    IReadOnlyList<string> TrackNames,
    bool VideoDecodable,
    string VideoCodec = "",
    IReadOnlyList<bool>? TracksPlayable = null);
