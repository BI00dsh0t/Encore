using Encore.Contracts.Messages;

namespace Encore.Core.Sessions;

/// <summary>A game proposed as the Target Game: its process, window, Game Profile id and display name.</summary>
public sealed record TargetCandidate(int ProcessId, nint Window, string ProfileId, string Name)
{
    /// <summary>The Target Game as <c>target.changed</c> and the snapshot carry it.</summary>
    public TargetGame ToTargetGame() => new(ProfileId, Name, ProcessId);
}
