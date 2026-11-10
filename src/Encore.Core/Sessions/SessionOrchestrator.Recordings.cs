using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Hotkeys;
using Encore.Core.Library;
using Encore.Core.Output;
using Encore.Core.Settings;

namespace Encore.Core.Sessions;

// Recordings on the game session: the Record press, the writer's start and result, and a full disk.
public sealed partial class SessionOrchestrator
{
    /// <summary>A Record press, in the mode read live from settings: toggle, start or stop. A stop is never gated.</summary>
    private void OnRecordPress(HotkeyAction action, long qpc)
    {
        if (_recordings is not { } recordings)
        {
            return;
        }

        // One document per press, so its mode and its profile's switch come from one revision.
        var document = _currentDocument();
        var mode = SettingsResolver.Resolve(document, null).RecordHotkeyMode;
        if (!HotkeyPlan.IsActive(action, mode))
        {
            recordings.Info?.Invoke($"The {action} press isn't used in the {mode} Record Hotkey mode; it was ignored");
            return;
        }

        if (_gameRecording.Running is not { } running)
        {
            if (action == HotkeyAction.RecordStop)
            {
                recordings.Info?.Invoke("The Record Stop press came with no Recording running; it was ignored");
                return;
            }

            StartRecording(qpc, document);
            return;
        }

        if (action == HotkeyAction.RecordStart)
        {
            recordings.Info?.Invoke("The Record Start press came while a Recording runs; it was ignored");
            return;
        }

        if (running.Reason is not null)
        {
            recordings.Info?.Invoke("The Record press came while the Recording was stopping; it was ignored");
            return;
        }

        running.Reason = RecordingStopReasons.Stopped;
        try
        {
            running.Write.Stop(qpc);
        }
        catch (Exception e)
        {
            // The writer still finishes when the session's output ends.
            recordings.Failed(e);
        }
    }

    /// <summary>Starts a Recording on the game session when its profile allows it; otherwise only the Info callback says why.</summary>
    private void StartRecording(long qpc, SettingsDocument document)
    {
        var recordings = _recordings!;
        if (_active is not { Spec.Kind: SessionKind.Game } active)
        {
            recordings.Info?.Invoke(_drain is not null
                ? "The Record press came while the game session was draining; no Recording was started"
                : "The Record press came with no game session; no Recording was started");
            return;
        }

        var profileId = active.Spec.Target.ProfileId;
        if (profileId is null || !document.Profiles.TryGetValue(profileId, out var profile))
        {
            recordings.Info?.Invoke("The Record key was pressed before Encore knew the game; no Recording was started");
            return;
        }

        if (!profile.RecordingsEnabled)
        {
            recordings.Info?.Invoke($"Recordings are off for {profile.Name}; the Record press was ignored");
            return;
        }

        if (BeginRecording(active, profileId, qpc, document) is { } recording)
        {
            SetState(_state with { Recording = RecordingState.Running });
            recordings.Started(new RecordingStarted(recording.Path));
        }
    }

    /// <summary>Begins the session's Recording at a path reserved now; null, with the reason reported, when none started.</summary>
    private RecordingRun? BeginRecording(Active active, string profileId, long qpc, SettingsDocument document)
    {
        var recordings = _recordings!;
        IRecordingWrite? write;
        string path;
        try
        {
            write = active.Session.Record(qpc);
            if (write is null)
            {
                recordings.Info?.Invoke("The game session can't record yet; no Recording was started");
                return null;
            }

            // One snapshot for the press; the Recording shares the session's chain whatever it asks for.
            var snapshot = SettingsResolver.Resolve(document, profileId);
            if (ChainKey.Of(SessionKind.Game, active.Spec.Target, snapshot) != ChainKey.Of(active.Spec))
            {
                recordings.Info?.Invoke("The profile's video or Tracks differ from the game session's; the Recording shares the session's");
            }

            var folder = snapshot.OutputFolder ?? recordings.DefaultOutputFolder();
            path = recordings.Paths.AllocateForGame(document, profileId, GameName(active), folder, qpc);
        }
        catch (Exception e)
        {
            recordings.Failed(e);
            return null;
        }

        if (OpenWrite(recordings, new RecordingRun(active.Id, path, write), profileId) is not { } recording)
        {
            return null;
        }

        _gameRecording.Start(recording);

        // A chain prepared before the press is dropped; the change waits for the Recording's end.
        try
        {
            DropPreparedChain(active);
        }
        catch (Exception)
        {
            // The session's own failure path ends it if it is broken.
        }

        active.Deferred = false;
        ApplySettings(active);
        return recording;
    }

    /// <summary>The chain was forcibly rebuilt: the running Recording ends, and a new one starts at once in a new file.</summary>
    private void OnChainRestarted(Active active, long qpc)
    {
        var profileId = active.Spec.Target.ProfileId;
        RestartRecording(
            _gameRecording,
            active,
            "Recording",
            () => profileId is null ? null : BeginRecording(active, profileId, qpc, _currentDocument()));
    }

    /// <summary>A session's Recording ends at an encoder restart and its replacement begins; what names it in the log.</summary>
    private void RestartRecording(RecordingSlot slot, Active session, string what, Func<RecordingRun?> begin)
    {
        if (_recordings is not { } recordings
            || slot.Running is not { } running
            || running.SessionId != session.Id
            || running.Reason is not null)
        {
            return;
        }

        running.Reason = RecordingStopReasons.EncoderRestart;
        if (!slot.TryMoveRunningToEnding())
        {
            // The last restart's Recording is still finishing: this one stops without a replacement.
            recordings.Info?.Invoke($"A second encoder restart came while the last one's {what} was still finishing; the {what} stops");
            return;
        }

        if (begin() is not { } next)
        {
            recordings.Info?.Invoke($"The encoder restart ended the {what} at {running.Path}, and no new one could start");
            return;
        }

        next.Follows = running;
    }

    /// <summary>A restart's new Recording, once the one it replaced is accounted for: published as started, then the handover.</summary>
    private void Announce(RecordingRun recording)
    {
        if (recording.Follows is not { } follows)
        {
            return;
        }

        recording.Follows = null;
        var recordings = _recordings!;
        var started = recording.Monitor ? recordings.MonitorStarted ?? recordings.Started : recordings.Started;
        started(new RecordingStarted(recording.Path));
        recordings.Restarted?.Invoke(new RecordingRestart(follows.Path, recording.Path));
    }

    /// <summary>A session ending under its running Recording stops it with the given reason.</summary>
    private void MarkSessionEnded(Active active, string reason)
    {
        if (_gameRecording.Running is { Reason: null } running && running.SessionId == active.Id)
        {
            running.Reason = reason;
        }
    }

    /// <summary>A Recording writer's result: tells the Library and publishes the stop; then what follows a Recording's end.</summary>
    private void OnRecordingWritten(RecordingWritten written, bool commitPending)
    {
        var recording = written.Recording;
        var slot = SlotOf(recording);
        var place = slot.Remove(recording);
        if (place == RecordingPlace.None)
        {
            return;
        }

        if (place == RecordingPlace.Running)
        {
            // A restart's replacement that ends before the one it replaced: started before it stopped.
            Announce(recording);
        }

        var recordings = _recordings!;
        var reason = CloseWrite(recordings, written);
        var off = slot.IsOff;
        if (off)
        {
            SetState(recording.Monitor
                ? _state with { MonitorRecording = RecordingState.Off }
                : _state with { Recording = RecordingState.Off });
        }

        PublishStopped(recordings, recording, reason);
        if (place == RecordingPlace.Ending && slot.ReplacementOf(recording) is { } next)
        {
            Announce(next);
        }

        if (!off)
        {
            return;
        }

        if (recording.Monitor)
        {
            if (_monitor?.Id == recording.SessionId)
            {
                EndMonitor("monitor recording stopped", RecordingStopReasons.SessionEnded);
            }
        }
        else
        {
            AfterGameRecording(recording, commitPending);
        }
    }

    // The stop goes to the Recording's own listener; a full disk is reported, and may stop a Rewind on that disk.
    private void PublishStopped(Recordings recordings, RecordingRun recording, string reason)
    {
        var stopped = recording.Monitor ? recordings.MonitorStopped ?? recordings.Stopped : recordings.Stopped;
        stopped(new RecordingStopped(recording.Path, reason));
        if (reason != ErrorCodes.DiskFull)
        {
            return;
        }

        if (recording.Monitor)
        {
            recordings.DiskFull?.Invoke();
        }
        else
        {
            OnDiskFull(recordings, recording);
        }
    }

    // No game Recording runs any more: a change deferred during it is prepared, or a held Target switch commits.
    private void AfterGameRecording(RecordingRun recording, bool commitPending)
    {
        // The session still runs and no switch is held: a change deferred during the Recording is prepared now.
        if (commitPending && _pendingTarget is null && _active is { } active && active.Id == recording.SessionId)
        {
            active.Deferred = false;
            ApplySettings(active);
        }

        if (commitPending && _pendingTarget is { } pending)
        {
            CommitTarget(pending);
        }
    }

    /// <summary>Begins a Recording's write in the Library and starts its writer; null, the reserved file removed, on failure.</summary>
    private RecordingRun? OpenWrite(Recordings recordings, RecordingRun recording, string? profileId)
    {
        var path = recording.Path;
        var library = recordings.Library ?? NoLibraryWrites.Instance;
        library.BeginWrite(path, profileId, recording.Monitor ? LibraryItemKinds.MonitorRecording : LibraryItemKinds.Recording);
        recordings.Open?.Opened(path);
        try
        {
            recording.Write.Start(path, error =>
            {
                // Before the post, so a shutdown that closed the channel still sees the result.
                recording.Done.TrySetResult(error);
                Post(new RecordingWritten(recording, error));
            });
        }
        catch (Exception e)
        {
            // It never started: nothing else will remove the reserved file.
            TryDelete(path);
            library.EndWrite(path, WriteOutcome.Failed);
            recordings.Open?.Closed(path);
            recordings.Failed(e);
            return null;
        }

        return recording;
    }

    /// <summary>Ends a finished Recording's write in the Library and returns the stop's reason: the failure's, else its own.</summary>
    private static string CloseWrite(Recordings recordings, RecordingWritten written)
    {
        var (recording, error) = (written.Recording, written.Error);
        var kept = error is null || File.Exists(recording.Path);
        (recordings.Library ?? NoLibraryWrites.Instance).EndWrite(recording.Path, kept ? WriteOutcome.Written : WriteOutcome.Failed);
        recordings.Open?.Closed(recording.Path);
        if (error is not null)
        {
            recordings.Failed(error);
        }

        return error is not null ? WriteFailures.ReasonOf(error) : recording.Reason ?? RecordingStopReasons.SessionEnded;
    }

    /// <summary>A Recording stopped on a full disk: with the ring on that disk, the session ends and waits for room.</summary>
    private void OnDiskFull(Recordings recordings, RecordingRun recording)
    {
        recordings.DiskFull?.Invoke();
        if (Volatile.Read(ref _disposed) != 0
            || _clips is null
            || _targetCapture is null
            || _target is not { } target
            || _active is not { Spec.Kind: SessionKind.Game } active
            || active.Id != recording.SessionId
            || active.Spec.Snapshot.ReplayStorage != ReplayStorage.Disk)
        {
            return;
        }

        // A restart's replacement still running here stops as session-ended: its own writer didn't fail.
        EndActive("disk full", failure: null, RecordingStopReasons.SessionEnded);
        _ringWait = target;
        var every = recordings.RingRecheck ?? Recordings.DefaultRingRecheck;
        _ringTimer = new Timer(_ => Post(new RingRecheck()), null, every, every);
        recordings.ReplayDiskFull?.Invoke(true);
        recordings.Info?.Invoke($"The disk filled during the Recording at {recording.Path}; Clips stopped and start again once the cache folder fits");
    }

    /// <summary>A recheck after a full disk: Rewind restarts on the Target once its ring fits, or the wait ends with the Target.</summary>
    private void RecheckRing(TargetCandidate wait)
    {
        if (_active is not null || _target is not { } target || target.ProcessId != wait.ProcessId)
        {
            ClearRingWait();
            return;
        }

        var snapshot = SettingsResolver.Resolve(_currentDocument(), target.ProfileId);
        if (snapshot.ReplayStorage != ReplayStorage.Disk || (_recordings?.RingFits?.Invoke(snapshot) ?? true))
        {
            ClearRingWait();
            StartOnTarget(target);
        }
    }

    /// <summary>Ends a wait for room for the ring, if any, and reports it.</summary>
    private void ClearRingWait()
    {
        if (_ringWait is null)
        {
            return;
        }

        _ringWait = null;
        _ringTimer?.Dispose();
        _ringTimer = null;
        _recordings?.ReplayDiskFull?.Invoke(false);
    }

    private void OnRingRecheck()
    {
        if (_ringWait is { } wait)
        {
            RecheckRing(wait);
        }
    }

    /// <summary>dev.simulate's display state only: a real Recording's state wins, and it never holds the Target.</summary>
    private void OnSimulatedRecording(RecordingRunning recording)
    {
        if (_gameRecording.IsOff)
        {
            SetState(_state with { Recording = recording.Running ? RecordingState.Running : RecordingState.Off });
        }
    }

    private RecordingSlot SlotOf(RecordingRun recording) => recording.Monitor ? _monitorRecording : _gameRecording;
}
