using System.Diagnostics;
using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.Core.Sessions;

// A lost GPU device, and the PC going to sleep and waking.
public sealed partial class SessionOrchestrator
{
    private void OnSessionDeviceLost(SessionDeviceLost lost)
    {
        if (_monitor is { } monitor && monitor.Id == lost.SessionId)
        {
            OnDeviceLost(monitor, lost.Failure, monitor: true);
        }
        else if (_active is { } active && active.Id == lost.SessionId)
        {
            OnDeviceLost(active, lost.Failure, monitor: false);
        }
    }

    private void OnRetryDeviceRecovery(RetryDeviceRecovery retry)
    {
        if (_monitor is { } monitor && monitor.Id == retry.SessionId)
        {
            TryRecover(monitor, monitor: true);
        }
        else if (_active is { } active && active.Id == retry.SessionId)
        {
            TryRecover(active, monitor: false);
        }
    }

    private void OnSessionChainRestarted(SessionChainRestarted restarted)
    {
        if (_monitor is { } monitor && monitor.Id == restarted.SessionId)
        {
            OnRecovered(monitor);
            OnMonitorChainRestarted(monitor, restarted.Qpc);
        }
        else if (_active is { } active && active.Id == restarted.SessionId)
        {
            OnRecovered(active);
            OnChainRestarted(active, restarted.Qpc);
        }
    }

    /// <summary>A session's GPU device was lost, or a recovery attempt failed: retry on a timer, and give up after a limit.</summary>
    private void OnDeviceLost(Active session, Exception failure, bool monitor)
    {
        // An attempt that failed as the PC went to sleep: the resume retries.
        if (session.Suspended)
        {
            return;
        }

        if (session.LostAt is null)
        {
            session.LostAt = Stopwatch.GetTimestamp();
            session.ResetRaised = true;
            _deviceRecovery.Reset?.Invoke(monitor
                ? $"The GPU driver reset; the Monitor Recording is rebuilding its capture and encoder ({failure.Message})"
                : $"The graphics driver reset; Encore is restarting its capture ({failure.Message})");
            TryRecover(session, monitor);
            return;
        }

        if (Stopwatch.GetElapsedTime(session.LostAt.Value) >= (_deviceRecovery.GiveUpAfter ?? DeviceRecovery.DefaultGiveUpAfter))
        {
            GiveUpRecovery(session, failure, monitor);
            return;
        }

        ScheduleRecovery(session);
    }

    /// <summary>Asks a lost session for one recovery attempt; a throw or refusal is retried, until the give-up time.</summary>
    private void TryRecover(Active session, bool monitor)
    {
        session.Retry?.Dispose();
        session.Retry = null;
        if (session.LostAt is not { } lostAt || session.Suspended)
        {
            return;
        }

        bool started;
        try
        {
            started = session.Session.RecoverDevice();
        }
        catch (Exception e)
        {
            Post(new SessionDeviceLost(session.Id, e));
            return;
        }

        if (started)
        {
            // Asked again after RetryEvery: refused while this attempt runs, so one that hangs still reaches the give-up.
            ScheduleRecovery(session);
            return;
        }

        if (Stopwatch.GetElapsedTime(lostAt) >= (_deviceRecovery.GiveUpAfter ?? DeviceRecovery.DefaultGiveUpAfter))
        {
            GiveUpRecovery(session, new TimeoutException("No recovery attempt succeeded in time."), monitor);
            return;
        }

        ScheduleRecovery(session);
    }

    /// <summary>A one-shot timer posting the session's next recovery attempt after <see cref="DeviceRecovery.RetryEvery"/>.</summary>
    private void ScheduleRecovery(Active session)
    {
        session.Retry?.Dispose();
        var id = session.Id;
        session.Retry = new Timer(
            _ => Post(new RetryDeviceRecovery(id)),
            null,
            _deviceRecovery.RetryEvery ?? DeviceRecovery.DefaultRetryEvery,
            System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>Recovery took too long: the game session ends and stays off until the Target's process changes.</summary>
    private void GiveUpRecovery(Active session, Exception failure, bool monitor)
    {
        var after = (_deviceRecovery.GiveUpAfter ?? DeviceRecovery.DefaultGiveUpAfter).TotalSeconds;
        string message;
        if (monitor)
        {
            message = $"The Monitor Recording couldn't recover from a GPU driver reset within {after:0} s; it stopped ({failure.Message})";
            EndMonitor("gpu reset failed", RecordingStopReasons.SessionEnded);
        }
        else
        {
            var what = session.Resuming
                ? $"restart capture within {after:0} s of the PC waking"
                : $"recover from a graphics driver reset within {after:0} s";
            message = $"Encore couldn't {what}; Clips are off until your next game ({failure.Message})";
            _gpuStoppedPid = session.Spec.Target.ProcessId;
            EndActive("gpu reset failed", new ErrorCodeException(ErrorCodes.GpuResetFailed, message, failure));
        }

        _deviceRecovery.Failed?.Invoke(message);
    }

    /// <summary>A session's chain restarted: when that was a GPU reset's recovery, the wait ends and the time taken is reported.</summary>
    private void OnRecovered(Active session)
    {
        if (session.LostAt is not { } lostAt)
        {
            return;
        }

        StopRecovery(session);

        // One that installed just as the PC went to sleep is torn down by the suspend, and the resume rebuilds.
        if (session.Suspended)
        {
            return;
        }

        // A resume's rebuild ends a GPU reset's outage too when one ran into the sleep: its Reset is answered.
        session.Resuming = false;
        if (!session.ResetRaised)
        {
            return;
        }

        session.ResetRaised = false;
        _deviceRecovery.Recovered?.Invoke(Stopwatch.GetElapsedTime(lostAt));
    }

    /// <summary>Ends a session's recovery wait, if any: no retry is posted after this.</summary>
    private static void StopRecovery(Active session)
    {
        session.LostAt = null;
        session.Retry?.Dispose();
        session.Retry = null;
    }

    /// <summary>The PC is going to sleep: Recordings stop, the Monitor session ends, and the game session suspends.</summary>
    private void OnSuspend(long qpc)
    {
        var ran = false;
        if (_gameRecording.Running is { Reason: null } running)
        {
            ran = true;
            running.Reason = RecordingStopReasons.Suspend;
            try
            {
                running.Write.Stop(qpc);
            }
            catch (Exception e)
            {
                // The writer still finishes when the session's output ends.
                _recordings?.Failed(e);
            }
        }

        if (_monitor is { } monitor)
        {
            ran |= _monitorRecording.Running is { Reason: null } monitorRunning && monitorRunning.SessionId == monitor.Id;
            EndMonitor("suspend", RecordingStopReasons.Suspend);
        }

        if (ran)
        {
            _recordings?.Warn?.Invoke(
                ErrorCodes.RecordingSuspended,
                "The PC went to sleep; the Recording ended at its last fragment and isn't restarted");
        }

        if (_active is not { Suspended: false } active)
        {
            return;
        }

        try
        {
            active.Suspended = active.Session.Suspend();
        }
        catch (Exception e)
        {
            _recordings?.Info?.Invoke($"The game session couldn't suspend for sleep: {e.Message}");
        }

        // Kept lost: the resume restarts the wait from then. A session that didn't suspend keeps its retries.
        if (active.Suspended)
        {
            active.Retry?.Dispose();
            active.Retry = null;
        }
    }

    /// <summary>The PC woke: a suspended game session rebuilds as after a GPU reset; a second resume is ignored.</summary>
    private void OnResume()
    {
        if (_active is not { Suspended: true } active)
        {
            return;
        }

        active.Suspended = false;
        active.Resuming = true;
        active.LostAt = Stopwatch.GetTimestamp();
        TryRecover(active, monitor: false);
    }
}
