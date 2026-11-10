namespace Encore.Platform.Audio;

/// <summary>An active MMDevice endpoint; ParentName is the audio device that exposes it, or empty.</summary>
public sealed record AudioEndpoint(string Id, string FriendlyName, AudioFlow Flow, string ParentName = "");
