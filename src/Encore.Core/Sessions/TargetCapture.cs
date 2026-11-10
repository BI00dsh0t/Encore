using Encore.Contracts.Settings;

namespace Encore.Core.Sessions;

/// <summary>The game session that follows the Target Game; each new Target ends it and starts one on the Target's window.</summary>
public sealed record TargetCapture(Func<EffectiveSettings, SessionAudio> Audio, Action<TargetCandidate, Exception> Failed, CaptureWait? Wait = null);
