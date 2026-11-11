using System.Diagnostics;
using Encore.Contracts.Settings;

namespace Encore.Core.Sessions;

public sealed partial class SessionOrchestrator
{
    private sealed class Active(long id, ICaptureSession session, SessionSpec spec, Func<EffectiveSettings, SessionAudio> audio)
    {
        public long Id { get; } = id;

        public ICaptureSession Session { get; } = session;

        /// <summary>The session's spec: its Target follows a retarget, its snapshot and audio a chain switch.</summary>
        public SessionSpec Spec { get; set; } = spec;

        /// <summary>The session's audio from a snapshot, as at Start.</summary>
        public Func<EffectiveSettings, SessionAudio> Audio { get; } = audio;

        /// <summary>Whether the session was last asked to prepare a chain, not to drop one.</summary>
        public bool Pending { get; set; }

        /// <summary>Monitors whose arrival a Monitor session has announced, by GDI device name.</summary>
        public HashSet<string> Announced { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether a chain change was deferred, and logged, during the running Recording.</summary>
        public bool Deferred { get; set; }

        /// <summary>The Target's display name at <see cref="StartOnTarget"/>; null for a session started otherwise.</summary>
        public string? GameName { get; init; }

        /// <summary>Whether its output anchored at its first frame.</summary>
        public bool Anchored { get; set; }

        /// <summary>The periodic timer posting its window checks once it anchored on a Target it follows; null for none.</summary>
        public Timer? Follow { get; set; }

        /// <summary>Whether it has no capture: its window closed, or a retarget failed, and no retarget has succeeded since.</summary>
        public bool WindowClosed { get; set; }

        /// <summary>Its failed retargets in a row, for the backoff.</summary>
        public int FollowFailures { get; set; }

        /// <summary>The <see cref="Stopwatch"/> timestamp before which no retarget is tried; 0 for none.</summary>
        public long FollowAfter { get; set; }

        /// <summary>The press QPC of the session's last Reset; null before its first Clip.</summary>
        public long? ResetQpc { get; set; }

        /// <summary>The <see cref="Stopwatch"/> timestamp its GPU device was lost at; null while it isn't lost.</summary>
        public long? LostAt { get; set; }

        /// <summary>The one-shot timer posting its next recovery attempt; null for none.</summary>
        public Timer? Retry { get; set; }

        /// <summary>Whether it was suspended for sleep and hasn't been asked to resume yet.</summary>
        public bool Suspended { get; set; }

        /// <summary>Whether its recovery is a resume's, not a GPU reset's, until it recovers or gives up.</summary>
        public bool Resuming { get; set; }

        /// <summary>Whether a loss was reported for the current outage and not yet answered by a recovery.</summary>
        public bool ResetRaised { get; set; }

        public TaskCompletionSource<SessionOutcome> Ended { get; } = NewReply<SessionOutcome>();
    }
}
