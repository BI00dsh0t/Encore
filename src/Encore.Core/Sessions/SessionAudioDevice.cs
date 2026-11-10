using Encore.Contracts.Settings;

namespace Encore.Core.Sessions;

/// <summary>One audio device a session captures; IsMic marks a Mic Device, and the names find it after a mixer restart.</summary>
public sealed record SessionAudioDevice(string EndpointId, AudioFlow Flow, string FriendlyName, bool IsMic = false, string ParentName = "");
