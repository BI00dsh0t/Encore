using Encore.Core.Sessions;

namespace Encore.Platform.Windows;

public sealed partial class GameMonitor
{
    /// <summary>One classified process; ProfileId is null, and it is never detected, when its path can't be made canonical.</summary>
    /// <remarks>ProfileId is the Game Profile the program belongs to, which a Store app's older version may name.</remarks>
    private sealed record SeenProcess(int Pid, IGameProcess Process, string? ProfileId, string RawPath, bool Eligible)
    {
        /// <summary>Set once the process is detected; it then stays detected until it exits or its profile is Ignored.</summary>
        public TargetCandidate? Candidate { get; set; }

        /// <summary>The timestamp of the last poll that saw it in front; 0 = never.</summary>
        public long LastFocused { get; set; }

        /// <summary>How it was found, one of the game sources; set when it is detected.</summary>
        public string? Source { get; set; }

        /// <summary>The program's own canonical path; null when it can't be made canonical.</summary>
        public string? Path { get; init; }

        /// <summary>Whether it is never a game (<see cref="NeverGames"/>): the fullscreen rule never detects it.</summary>
        public bool NonGame { get; init; }
    }
}
