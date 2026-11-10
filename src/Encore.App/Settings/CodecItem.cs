using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>A codec the page offers, with its label.</summary>
internal sealed record CodecItem(VideoCodec Codec, string Label);
