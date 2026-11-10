using Encore.Core.Sessions;

namespace Encore.Platform.Windows;

public sealed partial class GameMonitor
{
    /// <summary>One classified process; ProfileId is null, and it is never detected, when its path can't be made canonical.</summary>
    private sealed record SeenProcess(int Pid, IGameProcess Process, string? ProfileId, string RawPath, bool Eligible)
    {
        /// <summary>Set once the process is detected; it then stays detected until it exits or its profile is Ignored.</summary>
        public TargetCandidate? Candidate { get; set; }

        /// <summary>The timestamp of the last poll that saw it in front; 0 = never.</summary>
        public long LastFocused { get; set; }

        /// <summary>Whether it is one of <see cref="KnownNonGames"/>: the fullscreen rule never detects it.</summary>
        public bool NonGame { get; init; }
    }
}
