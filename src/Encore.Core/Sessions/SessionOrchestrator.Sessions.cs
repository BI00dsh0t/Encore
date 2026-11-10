using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Settings;

namespace Encore.Core.Sessions;

// Starting, retargeting, ending and draining sessions, and mic mute.
public sealed partial class SessionOrchestrator
{
    private RunningSession Start(StartSession start) => Start(start.Kind, start.Target, _ => start.Audio);

    /// <summary>The audio comes from the snapshot resolved here; gameName is kept for the session's Clips.</summary>
    private RunningSession Start(SessionKind kind, CaptureTarget target, Func<EffectiveSettings, SessionAudio> audio, string? gameName = null)
    {
        if (_active is not null)
        {
            throw new ErrorCodeException(ErrorCodes.SessionRunning, "A session is already running.");
        }

        // A drained ring never outlives the start of a new session, nor does a wait for room for one.
        ReleaseDrain();
        ClearRingWait();

        // Resolved here: a retarget never touches it, and a later settings change arrives only by a chain switch.
        var snapshot = SettingsResolver.Resolve(_currentDocument(), target.ProfileId);
        var spec = new SessionSpec(kind, target, snapshot, audio(snapshot), _state.MicMuted);
        var id = ++_nextSessionId;
        var session = _factory.Start(spec, new Events(this, id));
        var active = new Active(id, session, spec, audio) { GameName = gameName };
        _active = active;

        // A Monitor Recording running holds the other chain, so a size move tears down first.
        if (_monitor is not null)
        {
            session.SetBuildAhead(false);
        }

        SetState(_state with { Replay = kind == SessionKind.Game ? ReplayState.Buffering : ReplayState.Off });
        return new RunningSession(spec, active.Ended.Task);
    }

    private SessionSpec Retarget(CaptureTarget target)
    {
        var active = _active ?? throw new ErrorCodeException(ErrorCodes.SessionNotRunning, "No session is running.");
        if (target.ProcessId != active.Spec.Target.ProcessId)
        {
            throw new ErrorCodeException(
                ErrorCodes.RetargetOtherProcess,
                $"The window belongs to process {target.ProcessId}, not the session's process {active.Spec.Target.ProcessId}; that is a new session.");
        }

        try
        {
            active.Session.Retarget(target);
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.RetargetOtherProcess)
        {
            // Refused before the old capture was touched: the session goes on.
            throw;
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.SessionNotRunning)
        {
            // It had already stopped itself (its post-back is still queued): its own outcome stands.
            EndActive("retarget after stop", failure: null);
            throw;
        }
        catch (Exception e)
        {
            // The old capture is already stopped: the session can't go on.
            EndActive("failure: " + e.Message, e);
            throw;
        }

        // The profile belongs to the session (its snapshot was resolved for it), not to the retarget.
        active.Spec = active.Spec with { Target = target with { ProfileId = active.Spec.Target.ProfileId } };
        return active.Spec;
    }

    private SessionOutcome End(string reason)
    {
        if (_active is null)
        {
            throw new ErrorCodeException(ErrorCodes.SessionNotRunning, "No session is running.");
        }

        return EndActive(reason, failure: null);
    }

    /// <param name="recordingReason">The stop reason of a Recording running on the session.</param>
    private SessionOutcome EndActive(string reason, Exception? failure, string recordingReason = RecordingStopReasons.SessionEnded)
    {
        var active = _active!;
        _active = null;
        StopRecovery(active);
        active.Follow?.Dispose();
        active.Follow = null;
        MarkSessionEnded(active, recordingReason);

        SessionOutcome outcome;
        try
        {
            outcome = active.Session.End(reason);
        }
        catch (Exception e)
        {
            outcome = new SessionOutcome(null, reason, e);
        }

        if (failure is not null && outcome.Failure is null)
        {
            outcome = outcome with { Failure = failure };
        }

        // Published before Ended completes, so whoever awaits it sees the state already off.
        SetState(_state with { Replay = ReplayState.Off });
        active.Ended.TrySetResult(outcome);
        return outcome;
    }

    /// <summary>Whether <paramref name="active"/> is drained, not ended, when its Target goes: a game session with Replay Clips.</summary>
    private bool Drains(Active? active) => _clips is not null && active is { Spec.Kind: SessionKind.Game };

    /// <summary>Ends the game session's capture but keeps its Rewind for saves: it becomes the drain, and a timer ends it.</summary>
    private void DrainActive(string reason, string recordingReason)
    {
        var active = _active!;
        _active = null;
        StopRecovery(active);
        active.Follow?.Dispose();
        active.Follow = null;
        MarkSessionEnded(active, recordingReason);
        ReleaseDrain();

        SessionOutcome outcome;
        try
        {
            outcome = active.Session.Drain(reason);
        }
        catch (Exception e)
        {
            outcome = new SessionOutcome(null, reason, e);
        }

        if (outcome.Failure is not null)
        {
            // It never anchored, failed or couldn't stop cleanly: there is nothing to keep.
            SetState(_state with { Replay = ReplayState.Off });
            TryRelease(active);
            active.Ended.TrySetResult(outcome);
            return;
        }

        _drain = active;
        SetState(_state with { Replay = ReplayState.Draining });
        active.Ended.TrySetResult(outcome);
        var id = active.Id;
        _drainTimer = new Timer(_ => Post(new DrainExpired(id)), null, _clips!.ExitDrain ?? ReplayClips.DefaultExitDrain, System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>Frees the drained session's Rewind, if any; <see cref="ReplayState.Off"/> unless a session is active.</summary>
    private void ReleaseDrain()
    {
        if (_drain is not { } drain)
        {
            return;
        }

        _drain = null;
        _drainTimer?.Dispose();
        _drainTimer = null;
        TryRelease(drain);
        if (_active is null)
        {
            SetState(_state with { Replay = ReplayState.Off });
        }
    }

    private static void TryRelease(Active session)
    {
        try
        {
            session.Session.Release();
        }
        catch (Exception)
        {
            // Releasing is best effort: a save in flight keeps its own lease either way.
        }
    }

    /// <summary>A session's capture ended on its own: the Monitor session ends; the game session follows, drains or ends.</summary>
    private void OnSessionClosed(SessionClosed closed)
    {
        if (_monitor is { } monitor && monitor.Id == closed.SessionId)
        {
            // WGC can see the monitor go before the display change arrives.
            if (!EndMonitorIfRemoved(monitor))
            {
                EndMonitor(closed.Reason, RecordingStopReasons.SessionEnded);
            }
        }
        else if (_active is { } active && active.Id == closed.SessionId)
        {
            OnActiveClosed(active, closed.Reason);
        }
    }

    private void OnActiveClosed(Active active, string reason)
    {
        // A game whose process still runs is followed to its next window (TargetCapture.Wait).
        if (Follows(active))
        {
            OnWindowClosed(active);
        }

        // WGC can see the window close before Game Detection sees the exit: drain here too.
        else if (Drains(active))
        {
            DrainActive(reason, RecordingStopReasons.GameExit);
        }
        else
        {
            EndActive(reason, failure: null, RecordingStopReasons.GameExit);
        }
    }

    private void OnSessionFailed(SessionFailed failed)
    {
        if (_monitor is { } monitor && monitor.Id == failed.SessionId)
        {
            if (!EndMonitorIfRemoved(monitor))
            {
                _recordings?.Failed(failed.Failure);
                EndMonitor("failure: " + failed.Failure.Message, RecordingStopReasons.SessionEnded);
            }
        }
        else if (_active is { } active && active.Id == failed.SessionId)
        {
            OnActiveFailed(active, failed.Failure);
        }
    }

    private void OnSessionAnchored(SessionAnchored anchored)
    {
        if (_monitor is { } monitor && monitor.Id == anchored.SessionId)
        {
            OnMonitorAnchored(monitor, anchored.Qpc);
        }
        else if (_active is { } active && active.Id == anchored.SessionId)
        {
            OnActiveAnchored(active);
        }
    }

    private void OnDrainExpired(DrainExpired expired)
    {
        if (_drain?.Id == expired.SessionId)
        {
            ReleaseDrain();
        }
    }

    /// <summary>The saved mic mute; muted when there is no store or it fails.</summary>
    private static bool LoadMicMuted(IMicMuteStore? store)
    {
        if (store is null)
        {
            return true;
        }

        try
        {
            return store.Load();
        }
        catch (Exception)
        {
            // The store reports its own failures; a faulty one can't leave the mic open.
            return true;
        }
    }

    /// <summary>Flips mic mute: the session first, then the store, then the published state; a failure of either is swallowed.</summary>
    private void ToggleMicMute(long qpc)
    {
        var muted = !_state.MicMuted;
        if (_active is { } active)
        {
            try
            {
                active.Session.SetMicMuted(muted, qpc);
            }
            catch (Exception)
            {
                // The session's own failure path ends it if it is broken; mute is still committed.
            }
        }

        if (_monitor is { } monitor)
        {
            try
            {
                monitor.Session.SetMicMuted(muted, qpc);
            }
            catch (Exception)
            {
                // As for the game session.
            }
        }

        try
        {
            _micMute?.Save(muted);
        }
        catch (Exception)
        {
            // The store logs its own failures; a faulty one must not stop the orchestrator.
        }

        SetState(_state with { MicMuted = muted });
    }
}
