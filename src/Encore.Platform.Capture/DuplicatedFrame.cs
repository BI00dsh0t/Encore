namespace Encore.Platform.Capture;

/// <summary>A frame acquired from a duplication, valid until released; a pointer-only update has time 0 and no texture.</summary>
internal readonly record struct DuplicatedFrame(long LastPresentTime, nint Texture, VideoSize Size);
