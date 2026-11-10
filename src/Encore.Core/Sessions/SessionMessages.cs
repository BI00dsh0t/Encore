using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Sessions;

/// <summary>A message to the orchestrator. Any thread may post one; they are handled one at a time, in arrival order.</summary>
public abstract record SessionMessage;

/// <summary>Starts a session; replies with it, or fails with <see cref="ErrorCodes.SessionRunning"/> or the factory's failure.</summary>
public sealed record StartSession(
    SessionKind Kind,
    CaptureTarget Target,
    SessionAudio Audio,
    TaskCompletionSource<RunningSession> Reply) : SessionMessage;

/// <summary>Moves the running session to another window of its process; replies with the updated spec, or fails.</summary>
public sealed record RetargetSession(CaptureTarget Target, TaskCompletionSource<SessionSpec> Reply) : SessionMessage;

/// <summary>Ends the running session; replies with its outcome, or fails with <see cref="ErrorCodes.SessionNotRunning"/>.</summary>
public sealed record EndSession(string Reason, TaskCompletionSource<SessionOutcome> Reply) : SessionMessage;

/// <summary>A registered Hotkey was pressed; Qpc was read when it arrived, and Mic Mute toggles from that time.</summary>
public sealed record HotkeyPressed(HotkeyAction Action, long Qpc) : SessionMessage;

/// <summary>The PC is going to sleep; a running Recording stops at Qpc, and the game session suspends.</summary>
public sealed record PowerSuspending(long Qpc) : SessionMessage;

/// <summary>The PC woke from sleep: a suspended game session rebuilds its capture into a new ring generation.</summary>
public sealed record PowerResumed : SessionMessage;

/// <summary>The displays changed: a Monitor Recording whose monitor is gone ends; a game session is left as it is.</summary>
public sealed record DisplaysChanged : SessionMessage;

/// <summary>The settings were committed, on any thread; a profile's new Tracks or video apply from its next Clip.</summary>
public sealed record SettingsUpdated : SessionMessage;

/// <summary>Game Detection proposes a Target Game (none when it is gone); only the orchestrator commits and publishes it.</summary>
public sealed record ProposeTarget(TargetCandidate? To, TargetChangeReason Reason) : SessionMessage;

/// <summary>The simulated Recording state: shown while no real Recording runs; it never holds the Target.</summary>
public sealed record RecordingRunning(bool Running) : SessionMessage;
