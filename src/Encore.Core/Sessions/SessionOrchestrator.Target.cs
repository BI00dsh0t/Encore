using System.Diagnostics;
using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.Core.Sessions;

// The Target Game: committing it, and keeping the game session on a window of it that can be captured.
public sealed partial class SessionOrchestrator
{
    /// <summary>Commits a proposed Target Game; while a Recording runs, a switch to another game is held. docs/code-notes.md.</summary>
    private void CommitTarget(ProposeTarget propose)
    {
        var current = _target;
        var to = propose.To;
        var decision = TargetSwitch.Decide(current, to, propose.Reason, recording: !_gameRecording.IsOff);
        if (decision == TargetDecision.Held)
        {
            _pendingTarget = propose;
            return;
        }

        _pendingTarget = null;
        if (decision == TargetDecision.SameProcess)
        {
            if (to!.Window != current!.Window)
            {
                _target = current with { Window = to.Window };
                FollowTargetWindow(_target);
            }
        }
        else if (decision == TargetDecision.Switch)
        {
            SwitchTarget(current, to, propose.Reason);
        }
    }

    /// <summary>The Target becomes another game, or none: the old one's waits and its session end, and the new one starts.</summary>
    private void SwitchTarget(TargetCandidate? current, TargetCandidate? to, TargetChangeReason reason)
    {
        _target = to;

        // A Rewind given up on a GPU reset stays off only for the Target it stopped on.
        _gpuStoppedPid = null;

        // A wait for room for the ring belongs to the Target it stopped on, as does a wait for its window.
        ClearRingWait();
        _captureWait.End(captured: false);
        _targetChanged?.Invoke(new TargetChanged(current?.ToTargetGame(), to?.ToTargetGame(), reason));
        if (_targetCapture is null)
        {
            return;
        }

        // Only an exit to no game leaves a drain running (one a closed window started, say).
        if (to is not null || reason != TargetChangeReason.Exit)
        {
            ReleaseDrain();
        }

        if (_active is { Spec.Kind: SessionKind.Game })
        {
            // Only an exit with no other game drains; a switch or Ignored ends at once.
            var recordingReason = TargetSwitch.RecordingReasonFor(reason);
            if (to is null && reason == TargetChangeReason.Exit && Drains(_active))
            {
                DrainActive("target exited", recordingReason);
            }
            else
            {
                EndActive("target changed", failure: null, recordingReason);
            }
        }

        if (to is not null)
        {
            StartOnTarget(to);
        }
    }

    /// <summary>Detection named a new window of the Target's process: the game session starts on it, or moves to it.</summary>
    private void FollowTargetWindow(TargetCandidate target)
    {
        if (_targetCapture is null)
        {
            return;
        }

        if (_active is null)
        {
            // A Rewind a full disk stopped waits for its recheck; one a GPU reset stopped, for the next Target.
            if (_ringWait is null && _gpuStoppedPid != target.ProcessId)
            {
                StartOnTarget(target);
            }

            return;
        }

        if (_active.Spec.Kind != SessionKind.Game || _active.Spec.Target.ProcessId != target.ProcessId)
        {
            return;
        }

        if (Follows(_active))
        {
            FollowWindow(_active, _targetCapture.Wait!, target.Window);
            return;
        }

        try
        {
            Retarget(new CaptureTarget(target.ProcessId, target.Window, target.ProfileId));
        }
        catch (Exception e)
        {
            // A failed retarget has already ended the session where it couldn't go on.
            _targetCapture.Failed(target, e);
        }
    }

    /// <summary>Starts the game session on the Target, waiting for a window that can be captured when the options say so.</summary>
    private void StartOnTarget(TargetCandidate target)
    {
        var capture = _targetCapture!;
        var wait = capture.Wait;

        // A new window, a ring recheck or the timer: this try replaces any one still pending.
        _captureWait.CancelRetry();
        if (wait is not null)
        {
            var window = Find(wait, target);
            if (!window.Found)
            {
                WaitForCapture(target, window.Why ?? "no window of it can be captured", failed: false);
                return;
            }

            target = target with { Window = window.Window };
            if (_target?.ProcessId == target.ProcessId)
            {
                _target = target;
            }
        }

        try
        {
            Start(SessionKind.Game, new CaptureTarget(target.ProcessId, target.Window, target.ProfileId), capture.Audio, target.Name);
        }
        catch (Exception e) when (wait is not null && wait.Retry(e))
        {
            WaitForCapture(target, e.Message, failed: true);
        }
        catch (Exception e)
        {
            // The Target stays committed; only its capture failed.
            capture.Failed(target, e);
        }
    }

    /// <summary>The window of <paramref name="target"/>'s process to capture; a search that throws keeps the given window.</summary>
    private static CaptureWindow Find(CaptureWait wait, TargetCandidate target)
    {
        try
        {
            return wait.Find(target);
        }
        catch (Exception)
        {
            // The session's own start or retarget reports a window it can't capture.
            return CaptureWindow.Of(target.Window);
        }
    }

    /// <summary>Whether a game session follows the Target's process: capture waits, and it was started on that process.</summary>
    private bool Follows(Active active) =>
        _targetCapture?.Wait is not null
        && Volatile.Read(ref _disposed) == 0
        && active is { Spec.Kind: SessionKind.Game, GameName: not null }
        && _target is { } target
        && target.ProcessId == active.Spec.Target.ProcessId;

    /// <summary>The game session failed; one that follows the Target, with a failure worth retrying, restarts after a backoff.</summary>
    private void OnActiveFailed(Active active, Exception failure)
    {
        var retry = Follows(active) && _targetCapture!.Wait!.Retry(failure);
        EndActive("failure: " + failure.Message, failure);
        if (retry)
        {
            WaitForCapture(_target!, failure.Message, failed: true);
        }
    }

    /// <summary>The followed window closed: before the first frame the session restarts; after it, it moves to the next window.</summary>
    private void OnWindowClosed(Active active)
    {
        var wait = _targetCapture!.Wait!;
        if (!active.Anchored)
        {
            EndActive("window closed", failure: null);
            WaitForCapture(_target!, "the window closed before its first frame", failed: true);
            return;
        }

        active.WindowClosed = true;
        FollowWindow(active, wait, active.Spec.Target.Window);
    }

    /// <summary>The game session got its first frame: a wait for its Target's window is over, and its window is now watched.</summary>
    private void OnActiveAnchored(Active active)
    {
        active.Anchored = true;
        if (Follows(active) && active.Follow is null)
        {
            var poll = _targetCapture!.Wait!.Poll;
            var id = active.Id;
            active.Follow = new Timer(_ => Post(new FollowCheck(id)), null, poll, poll);
        }

        if (_captureWait.Target is { } waiting && waiting.ProcessId == active.Spec.Target.ProcessId)
        {
            _captureWait.End(captured: true);
        }
    }

    /// <summary>Keeps the game session on a window it can capture, retargeting so Rewind is kept; with none, it waits.</summary>
    private void FollowWindow(Active active, CaptureWait wait, nint prefer)
    {
        if (active.LostAt is not null || active.Suspended || _target is not { } target)
        {
            return;
        }

        var found = Find(wait, target with { Window = prefer });
        if (!found.Found)
        {
            _captureWait.Begin(target, found.Why ?? "no window of it can be captured");
            return;
        }

        if (found.Window == active.Spec.Target.Window && !active.WindowClosed)
        {
            if (_captureWait.Target is { } same && same.ProcessId == target.ProcessId)
            {
                _captureWait.End(captured: true);
            }

            return;
        }

        if (Stopwatch.GetTimestamp() < active.FollowAfter)
        {
            return;
        }

        if (!TryRetarget(active, wait, target, found.Window))
        {
            return;
        }

        active.WindowClosed = false;
        active.FollowFailures = 0;
        active.FollowAfter = 0;
        active.Spec = active.Spec with { Target = active.Spec.Target with { Window = found.Window } };
        _target = target with { Window = found.Window };
        if (_captureWait.Target is { } waiting && waiting.ProcessId == target.ProcessId)
        {
            _captureWait.End(captured: true);
        }
    }

    // Moves the session to a window; false when it had stopped (it starts again) or the move failed (it is tried later).
    private bool TryRetarget(Active active, CaptureWait wait, TargetCandidate target, nint window)
    {
        try
        {
            active.Session.Retarget(new CaptureTarget(target.ProcessId, window, target.ProfileId));
            return true;
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.SessionNotRunning)
        {
            // It had already stopped itself (its post-back is still queued): start again.
            EndActive("retarget after stop", failure: null);
            WaitForCapture(target, e.Message, failed: true);
            return false;
        }
        catch (Exception e)
        {
            // No capture runs now, but the session and its Rewind go on: try again later.
            active.FollowFailures++;
            active.FollowAfter = Stopwatch.GetTimestamp() + (long)(wait.Backoff(active.FollowFailures).TotalSeconds * Stopwatch.Frequency);
            active.WindowClosed = true;
            _captureWait.Begin(target, e.Message);
            return false;
        }
    }

    /// <summary>Waits for the Target's window with no session: the run's first wait is reported, and the next try is posted.</summary>
    private void WaitForCapture(TargetCandidate target, string why, bool failed)
    {
        _captureWait.Begin(target, why);
        _captureWait.ScheduleRetry(failed);
    }

    /// <summary>The capture wait's timer fired: the game session starts on the Target if it is still the one waited for.</summary>
    private void RetryCapture(TargetCandidate waiting)
    {
        _captureWait.CancelRetry();
        if (_active is { Spec.Kind: SessionKind.Game } running && running.Spec.Target.ProcessId == waiting.ProcessId)
        {
            // A tick posted before the session started: the session follows the window now.
            return;
        }

        if (_active is not null || _targetCapture is null || _target is not { } target || target.ProcessId != waiting.ProcessId)
        {
            // A session runs (one started by hand), or the Target moved on without a commit ending the wait.
            _captureWait.End(captured: false);
            return;
        }

        if (_ringWait is not null || _gpuStoppedPid == target.ProcessId)
        {
            // A full disk or a given-up GPU reset holds Rewind off; their own waits restart it.
            _captureWait.End(captured: false);
            return;
        }

        StartOnTarget(target);
    }

    private void OnCaptureRetry()
    {
        if (_captureWait.Target is { } waiting)
        {
            RetryCapture(waiting);
        }
    }

    private void OnFollowCheck(FollowCheck check)
    {
        if (_active is { } active && active.Id == check.SessionId && Follows(active))
        {
            FollowWindow(active, _targetCapture!.Wait!, active.Spec.Target.Window);
        }
    }
}
