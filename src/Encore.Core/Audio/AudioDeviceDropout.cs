using Encore.Contracts.Settings;

namespace Encore.Core.Audio;

/// <summary>An open audio device the hub dropped or resumed: its endpoint, flow and names.</summary>
public sealed record AudioDeviceDropout(string EndpointId, AudioFlow Flow, string FriendlyName, string ParentName);
