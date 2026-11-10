namespace Encore.Core.Sessions;

/// <summary>One Track of a session: its title and the devices it mixes, as indexes into the session's devices.</summary>
public sealed record SessionTrack(string Name, IReadOnlyList<int> DeviceIndexes, bool MuteAffected = false);
