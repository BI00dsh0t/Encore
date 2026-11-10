using System.Diagnostics;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Library;
using Encore.Core.Output;
using Encore.Core.Settings;

namespace Encore.Core.Sessions;

// Saving Clips, and the game session's encoder chain when its settings change.
public sealed partial class SessionOrchestrator
{
    /// <summary>A save press: checks the cooldown and limits, reserves the path, then hands the session the write.</summary>
    private void SaveClip(long qpc)
    {
        var active = _active is { Spec.Kind: SessionKind.Game } game ? game : _drain;
        if (_clips is not { } clips || active is null)
        {
            return;
        }

        var document = _currentDocument();
        if (!AcceptSavePress(clips, document, qpc))
        {
            return;
        }

        if (_writing >= ReplayClips.MaxWriters)
        {
            // The ring keeps capturing; nothing is cut and nothing is Reset.
            clips.NotSaved?.Invoke(new ClipFailed(ErrorCodes.SaveBusy));
            return;
        }

        if (active.ResetQpc is { } reset && qpc - reset < Stopwatch.Frequency)
        {
            clips.NotSaved?.Invoke(new ClipFailed(ErrorCodes.ClipTooShort));
            return;
        }

        if (ReserveClip(clips, active, document, qpc) is var (write, path))
        {
            StartClipWrite(clips, active, write, path, qpc);
        }
    }

    // The Save Cooldown, read at each press (Global, Immediate): an accepted press publishes when it ends.
    private bool AcceptSavePress(ReplayClips clips, SettingsDocument document, long qpc)
    {
        var global = SettingsResolver.Resolve(document, null);
        var cooldown = Math.Max(0, global.SaveCooldown) * Stopwatch.Frequency;
        if (_saveCooldown.Accept(qpc, cooldown) is { } sinceLastSave)
        {
            clips.Ignored?.Invoke(sinceLastSave);
            return false;
        }

        // Measured in Hotkey QPC from the accepted press.
        DateTimeOffset? until = cooldown == 0
            ? null
            : DateTimeOffset.UtcNow + TimeSpan.FromSeconds((qpc + cooldown - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
        SetState(_state with { CooldownUntil = until });
        clips.Saving?.Invoke(new ClipSaving(DateTimeOffset.UtcNow));
        return true;
    }

    // Asks the session for the press's write and reserves its file; null, with the reason reported, when there is no Clip.
    private (IClipWrite Write, string Path)? ReserveClip(ReplayClips clips, Active active, SettingsDocument document, long qpc)
    {
        try
        {
            var write = active.Session.Save(qpc);
            if (write is null)
            {
                // Nothing to save yet; the accepted press still gets its outcome.
                clips.NotSaved?.Invoke(new ClipFailed(ErrorCodes.ClipTooShort));
                return null;
            }

            var folder = SettingsResolver.Resolve(document, null).OutputFolder ?? clips.DefaultOutputFolder();
            var profileId = active.Spec.Target.ProfileId;
            return (write, clips.Paths.AllocateForGame(document, profileId, GameName(active), folder, qpc));
        }
        catch (Exception e)
        {
            ReportSaveFailure(e);
            return null;
        }
    }

    // Tells the Library a write began and starts the writer, which posts its result back.
    private void StartClipWrite(ReplayClips clips, Active active, IClipWrite write, string path, long qpc)
    {
        var sessionId = active.Id;
        var library = clips.Library ?? NoLibraryWrites.Instance;
        library.BeginWrite(path, active.Spec.Target.ProfileId, LibraryItemKinds.Clip);
        try
        {
            write.Write(path, error => Post(new ClipWritten(sessionId, qpc, write, path, error)));
            _writing++;
        }
        catch (Exception e)
        {
            // It never started: nothing else will remove the reserved file.
            TryDelete(path);
            library.EndWrite(path, WriteOutcome.Failed);
            TakeSwitch(active, write);
            ReportSaveFailure(e);
            return;
        }

        TakeSwitch(active, write);
        SetState(_state with { ClipsWriting = _writing });
    }

    /// <summary>When a write's press switched the encoder chain, the spec takes the new snapshot and audio; the Target is kept.</summary>
    private static void TakeSwitch(Active active, IClipWrite write)
    {
        if (write.SwitchesTo is { } switched)
        {
            active.Spec = active.Spec with { Snapshot = switched.Snapshot, Audio = switched.Audio };
            active.Pending = false;
        }
    }

    /// <summary>A save that made no Clip: a real failure is logged, and every one is reported with its reason.</summary>
    private void ReportSaveFailure(Exception e)
    {
        if (e is not ErrorCodeException { Code: ErrorCodes.ClipTooShort or ErrorCodes.SaveBusy })
        {
            _clips?.Failed(e);
        }

        _clips?.NotSaved?.Invoke(new ClipFailed(WriteFailures.ReasonOf(e)));
    }

    /// <summary>The game's name: the one the session started on, else the Target's, else the profile's executable name.</summary>
    private string GameName(Active active)
    {
        if (active.GameName is { } name)
        {
            return name;
        }

        var target = active.Spec.Target;
        return _target is { } candidate && candidate.ProcessId == target.ProcessId
            ? candidate.Name
            : target.ProfileId is { } profileId ? Path.GetFileName(profileId) : "Game";
    }

    /// <summary>A writer's result: a written Clip Resets Rewind at its end; a failed write Resets nothing.</summary>
    private void OnClipWritten(ClipWritten written)
    {
        // Whatever the session: the write is no longer in flight.
        _writing--;
        SetState(_state with { ClipsWriting = _writing });
        var outcome = written.Error is null ? WriteOutcome.Written : WriteOutcome.Failed;
        (_clips?.Library ?? NoLibraryWrites.Instance).EndWrite(written.Path, outcome);
        if (written.Error is not null)
        {
            ReportSaveFailure(written.Error);
            return;
        }

        // The session still running, or the drained one: either still holds its Rewind.
        var active = _active?.Id == written.SessionId ? _active : _drain?.Id == written.SessionId ? _drain : null;
        if (active is not null)
        {
            // The running session takes the Effective Buffer Window at each Reset; a drained one keeps its ring as is.
            if (ReferenceEquals(active, _active))
            {
                active.Session.SetBufferWindow(SettingsResolver.Resolve(_currentDocument(), active.Spec.Target.ProfileId).BufferWindow);
            }

            written.Write.Reset();
            active.ResetQpc = active.ResetQpc is { } reset ? Math.Max(reset, written.Qpc) : written.Qpc;

            // Each Reset resolves the settings again, so a failed build is retried and a held change switches here.
            if (ReferenceEquals(active, _active))
            {
                ApplySettings(active, atReset: true);
            }
        }

        _clips?.Saved(new ClipSaved(written.Path));
    }

    private void OnSettingsUpdated()
    {
        if (_active is { } active)
        {
            ApplySettings(active);
        }
    }

    /// <summary>Resolves the game session's settings again and prepares or drops an encoder chain to match.</summary>
    private void ApplySettings(Active active, bool atReset = false)
    {
        if (_clips is null || active.Spec.Kind != SessionKind.Game)
        {
            return;
        }

        SessionSpec next;
        try
        {
            var snapshot = SettingsResolver.Resolve(_currentDocument(), active.Spec.Target.ProfileId);
            next = active.Spec with { Snapshot = snapshot, Audio = active.Audio(snapshot) };
        }
        catch (Exception)
        {
            // An invalid Track Layout: the session keeps its chain.
            return;
        }

        try
        {
            ApplyChainChange(active, next, atReset);
        }
        catch (Exception)
        {
            // The session's own failure path ends it if it is broken.
        }
    }

    // What the settings resolved now mean for the session's encoder chain: keep it, wait, switch at a Reset, or prepare ahead.
    private void ApplyChainChange(Active active, SessionSpec next, bool atReset)
    {
        var recordingOnSession = _gameRecording.Running?.SessionId == active.Id;
        switch (ChainChange.Decide(ChainKey.SameChain(next, active.Spec), recordingOnSession, monitorRuns: _monitor is not null))
        {
            case ChainDecision.Keep:
                DropPreparedChain(active);
                break;

            case ChainDecision.HeldByRecording:
                DropPreparedChain(active);
                DeferChainChange(active);
                break;

            case ChainDecision.SwitchAtReset:
                DropPreparedChain(active);
                if (atReset && active.Session.SwitchChain(next))
                {
                    active.Spec = active.Spec with { Snapshot = next.Snapshot, Audio = next.Audio };
                }

                break;

            default:
                active.Pending = true;
                active.Session.PrepareChain(next);
                break;
        }
    }

    // Tells the session to drop a chain it was asked to prepare; a no-op when it was asked for none.
    private static void DropPreparedChain(Active active)
    {
        if (active.Pending)
        {
            active.Pending = false;
            active.Session.PrepareChain(null);
        }
    }

    // Said once per Recording: the change applies when the Recording stops.
    private void DeferChainChange(Active active)
    {
        if (!active.Deferred)
        {
            active.Deferred = true;
            _recordings?.Info?.Invoke(
                "This game's video quality or audio tracks changed during a Recording; the change applies once the Recording stops");
        }
    }
}
