namespace Encore.App.Library;

/// <summary>
/// A Microsoft Store video extension Windows needs to decode a codec (story 14.6, AD-27): the codec's plain name,
/// the extension's name and its Store page, opened through the shell. HEVC and AV1 are the codecs our files use
/// that Windows doesn't decode out of the box; H.264 always plays.
/// </summary>
internal sealed record CodecExtension(string Codec, string Name, string StoreLink)
{
    public static CodecExtension Hevc { get; } = new("HEVC", "HEVC Video Extension", "ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7");

    public static CodecExtension Av1 { get; } = new("AV1", "AV1 Video Extension", "ms-windows-store://pdp/?ProductId=9MVZQVXJBQ9V");

    /// <summary>The extension for Windows' name of a video codec ("HEVC", "H265", "AV1", "AV01"); null for any other.</summary>
    public static CodecExtension? For(string? codec) => codec?.ToUpperInvariant() switch
    {
        "HEVC" or "H265" or "HVC1" or "HEV1" => Hevc,
        "AV1" or "AV01" => Av1,
        _ => null,
    };
}
