using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Hotkeys;
using Encore.Core.Monitors;
using Encore.Core.Settings;

namespace Encore.Core.Sessions;

// The Monitor session and its Monitor Recording.
public sealed partial class SessionOrchestrator
{
    /// <summary>A Monitor Record press, handled as a Record press; a stop before the session anchored just ends it.</summary>
    private void OnMonitorPress(HotkeyAction action, long qpc)
    {
        if (_recordings is not { } recordings)
        {
            return;
        }

        var document = _currentDocument();
        var mode = SettingsResolver.Resolve(document, null).RecordHotkeyMode;
        if (!HotkeyPlan.IsActive(action, mode))
        {
            recordings.Info?.Invoke($"The {action} press isn't used in the {mode} Record Hotkey mode; it was ignored");
            return;
        }

        if (_monitor is null)
        {
            StartMonitorOnPress(action, recordings, document);
        }
        else if (action == HotkeyAction.MonitorRecordStart)
        {
            recordings.Info?.Invoke("The Monitor Record Start press came while a Monitor Recording runs; it was ignored");
        }
        else
        {
            StopMonitorOnPress(recordings, qpc);
        }
    }

    // No Monitor session runs: a stop press has nothing to stop, and a start waits for the last file to finish.
    private void StartMonitorOnPress(HotkeyAction action, Recordings recordings, SettingsDocument document)
    {
        if (action == HotkeyAction.MonitorRecordStop)
        {
            recordings.Info?.Invoke("The Monitor Record Stop press came with no Monitor Recording running; it was ignored");
            return;
        }

        if (!_monitorRecording.IsOff)
        {
            // Its session ended (closed or failed) and its writer hasn't posted back yet.
            recordings.Info?.Invoke(
                "The Monitor Record press came while the last Monitor Recording was still finishing; it was ignored");
            return;
        }

        StartMonitor(document);
    }

    // A press that stops the Monitor Recording; one that is already stopping is left to finish.
    private void StopMonitorOnPress(Recordings recordings, long qpc)
    {
        if (_monitorRecording.Running is not { } running)
        {
            // Not anchored yet: nothing was written, and nothing is left.
            EndMonitor("monitor recording stopped", RecordingStopReasons.Stopped);
            return;
        }

        if (running.Reason is not null)
        {
            recordings.Info?.Invoke("The Monitor Record press came while the Monitor Recording was stopping; it was ignored");
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

    /// <summary>Starts the Monitor session on the Global settings, unless the encoder budget is spent.</summary>
    private void StartMonitor(SettingsDocument document)
    {
        var recordings = _recordings!;

        // No size move builds a chain ahead from here on, so none slips in before the budget check.
        _active?.Session.SetBuildAhead(false);
        if (_active is { } game && !FitsEncoderBudget(game, recordings))
        {
            return;
        }

        Active monitor;
        try
        {
            var snapshot = SettingsResolver.Resolve(document, null);
            if (!ResolveScreens(snapshot, out var screens))
            {
                _active?.Session.SetBuildAhead(true);
                return;
            }

            var spec = new SessionSpec(
                SessionKind.Monitor,
                new CaptureTarget(0, 0, null),
                snapshot,
                MonitorAudio(snapshot),
                _state.MicMuted)
            {
                Screens = screens,
            };
            var id = ++_nextSessionId;
            var session = _factory.Start(spec, new Events(this, id));
            monitor = new Active(id, session, spec, MonitorAudio);
        }
        catch (Exception e)
        {
            _active?.Session.SetBuildAhead(true);
            recordings.Failed(e);
            return;
        }

        _monitor = monitor;
        SetState(_state with { MonitorRecording = RecordingState.Running });
    }

    // Whether one more encoder chain fits beside the game session's; when not, the game session gets its chain back.
    private bool FitsEncoderBudget(Active game, Recordings recordings)
    {
        int chains;
        try
        {
            DropPreparedChain(game);
            chains = game.Session.Chains;
        }
        catch (Exception)
        {
            // The session's own failure path ends it if it is broken.
            chains = 1;
        }

        if (chains + 1 <= MaxChains)
        {
            return true;
        }

        recordings.Warn?.Invoke(
            ErrorCodes.EncoderBudget,
            "The Monitor Recording didn't start: "
            + $"your graphics card's video encoder is already busy with this game ({chains} videos).");

        // The chain dropped above is asked for again: nothing runs beside the game session.
        game.Session.SetBuildAhead(true);
        ApplySettings(game);
        return false;
    }

    /// <summary>Resolves the screens a Monitor Recording composites; a missing one is skipped with a warning.</summary>
    private bool ResolveScreens(EffectiveSettings snapshot, out IReadOnlyList<DisplayMonitor>? screens)
    {
        screens = null;
        if (snapshot.MonitorRecordingScreens is not { } chosen || _displays is not { } displays)
        {
            return true;
        }

        var recordings = _recordings!;
        var attached = displays.List().Where(screen => screen.Width > 0 && screen.Height > 0).ToList();
        var resolved = MonitorScreens.Resolve(chosen, snapshot.MonitorRecordingMonitor, attached);
        foreach (var missing in resolved.Missing)
        {
            recordings.Warn?.Invoke(
                ErrorCodes.MonitorMissing,
                $"The chosen screen {missing} isn't connected; the Monitor Recording skips it");
        }

        if (resolved.Screens.Count == 0)
        {
            recordings.Failed(new ErrorCodeException(
                ErrorCodes.MonitorRecordingNoScreens,
                "The Monitor Recording didn't start: none of the screens chosen to record is connected."));
            return false;
        }

        screens = resolved.Screens;
        return true;
    }

    /// <summary>The Monitor session's audio: the Global Track Layout's.</summary>
    private static SessionAudio MonitorAudio(EffectiveSettings snapshot) => SessionAudio.FromLayout(snapshot.TrackLayout);

    /// <summary>The Monitor session's first frame: its Recording starts there; one that can't start ends the session.</summary>
    private void OnMonitorAnchored(Active monitor, long qpc)
    {
        if (_monitorRecording.Running is not null)
        {
            return;
        }

        if (BeginMonitorRecording(monitor, qpc) is not { } recording)
        {
            EndMonitor("monitor recording failed", RecordingStopReasons.SessionEnded);
            return;
        }

        var recordings = _recordings!;
        (recordings.MonitorStarted ?? recordings.Started)(new RecordingStarted(recording.Path));
    }

    /// <summary>Begins a Monitor Recording at a path reserved now; null, with the reason reported, when none started.</summary>
    private RecordingRun? BeginMonitorRecording(Active monitor, long qpc)
    {
        var recordings = _recordings!;
        IRecordingWrite? write;
        string path;
        try
        {
            write = monitor.Session.Record(qpc);
            if (write is null)
            {
                recordings.Info?.Invoke("The Monitor session can't record; no Monitor Recording was started");
                return null;
            }

            var folder = SettingsResolver.Resolve(_currentDocument(), null).OutputFolder ?? recordings.DefaultOutputFolder();
            path = recordings.Paths.AllocateForMonitor(folder, qpc);
        }
        catch (Exception e)
        {
            recordings.Failed(e);
            return null;
        }

        if (OpenWrite(recordings, new RecordingRun(monitor.Id, path, write) { Monitor = true }, profileId: null) is not { } recording)
        {
            return null;
        }

        _monitorRecording.Start(recording);
        return recording;
    }

    /// <summary>The Monitor session's chain was rebuilt: its Recording ends and a new one starts at once in a new file.</summary>
    private void OnMonitorChainRestarted(Active monitor, long qpc) =>
        RestartRecording(_monitorRecording, monitor, "Monitor Recording", () => BeginMonitorRecording(monitor, qpc));

    /// <summary>Ends the Monitor session when its monitor is no longer attached; false when it is, or it can't be told.</summary>
    private bool EndMonitorIfRemoved(Active monitor)
    {
        // An empty list is a read that failed mid-change, not every monitor gone.
        if (_displays is not { } displays
            || !TryReadMonitors(monitor, displays, out var devices, out var attached)
            || devices.Count == 0
            || attached.Count == 0)
        {
            return false;
        }

        var change = MonitorChange.Compare(devices, attached);

        // Under All screens, a monitor that arrives joins at the next Monitor Recording.
        if (monitor.Spec.Screens is not null && monitor.Spec.Snapshot.MonitorRecordingScreens?.Mode == MonitorScreensMode.All)
        {
            AnnounceArrivals(monitor, change.Arrived);
        }

        var gone = change.Gone;
        if (gone.Count == 0)
        {
            return false;
        }

        // Some of several composited monitors gone: their areas turn black and the Recording goes on.
        if (gone.Count < devices.Count)
        {
            DropMonitors(monitor, gone);
            return false;
        }

        EndMonitor("monitor removed", RecordingStopReasons.MonitorRemoved);
        _recordings?.Warn?.Invoke(
            ErrorCodes.MonitorRemoved,
            $"Screen {string.Join(", ", gone)} was removed; the Monitor Recording ended at its last frame and its file was kept");
        return true;
    }

    // The Monitor session's monitors and the ones attached now; false, with a log line, when they can't be read.
    private bool TryReadMonitors(
        Active monitor,
        IDisplayMonitors displays,
        out IReadOnlyList<string> devices,
        out IReadOnlyList<DisplayMonitor> attached)
    {
        devices = [];
        attached = [];
        try
        {
            devices = monitor.Session.MonitorDevices;
            attached = displays.List();
            return true;
        }
        catch (Exception e)
        {
            _recordings?.Info?.Invoke($"The monitors couldn't be read after a display change: {e.Message}");
            return false;
        }
    }

    // Each monitor that went is dropped from the picture, with a warning for each the session did drop.
    private void DropMonitors(Active monitor, IReadOnlyList<string> gone)
    {
        foreach (var device in gone)
        {
            if (monitor.Session.DropMonitor(device))
            {
                _recordings?.Warn?.Invoke(
                    ErrorCodes.MonitorRemoved,
                    $"Screen {device} was removed; its part of the Monitor Recording is black and the Monitor Recording goes on");
            }
        }
    }

    // Each arrival is announced once per Monitor session.
    private void AnnounceArrivals(Active monitor, IReadOnlyList<string> arrived)
    {
        foreach (var device in arrived)
        {
            if (monitor.Announced.Add(device))
            {
                _recordings?.Warn?.Invoke(
                    ErrorCodes.MonitorAddedNextRecording,
                    $"Screen {device} was connected; it joins at the next Monitor Recording "
                    + "(the video's size can't change inside a file)");
            }
        }
    }

    /// <summary>Ends the Monitor session and stops its Recording; the game session's settings are then resolved again.</summary>
    private void EndMonitor(string reason, string recordingReason)
    {
        var monitor = _monitor!;
        _monitor = null;
        _active?.Session.SetBuildAhead(true);
        StopRecovery(monitor);
        if (_monitorRecording.Running is { Reason: null } running && running.SessionId == monitor.Id)
        {
            running.Reason = recordingReason;
        }

        SessionOutcome outcome;
        try
        {
            outcome = monitor.Session.End(reason);
        }
        catch (Exception e)
        {
            outcome = new SessionOutcome(null, reason, e);
        }

        if (_monitorRecording.IsOff)
        {
            SetState(_state with { MonitorRecording = RecordingState.Off });
        }

        monitor.Ended.TrySetResult(outcome);
        if (Volatile.Read(ref _disposed) == 0 && _active is { } game)
        {
            ApplySettings(game);
        }
    }

    private void OnDisplaysChanged()
    {
        if (_monitor is { } monitor)
        {
            EndMonitorIfRemoved(monitor);
        }
    }
}
