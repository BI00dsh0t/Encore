using Microsoft.Extensions.Logging;
using Encore.Contracts.Messages;
using Encore.Core.Sessions;

namespace Encore.Platform.Windows;

public sealed partial class GameMonitor
{
    // A known game in front: capture its window from now on, and propose it again when it is the Target.
    private void Follow(SeenProcess game, nint window)
    {
        var moved = game.Candidate!.Window != window;
        game.Candidate = game.Candidate with { Window = window };
        if (!moved)
        {
            return;
        }

        if (_proposed == game.Pid)
        {
            // Its first reason: a switch held by a Recording still commits as what it was.
            Propose(game.Candidate, _proposedReason);
        }
        else if (Committed() == game.Pid)
        {
            Propose(game.Candidate, TargetChangeReason.Detected);
        }
    }

    // The committed Target's process: read from the orchestrator when given, else the last proposal.
    private int? Committed() => _target is null ? _proposed : _target();

    private void Decide(long now)
    {
        if (_proposed is null)
        {
            if (_seen.Values.Any(seen => seen.Candidate is not null))
            {
                Propose(Detected(_focusProcessId) ?? Best(), _firstPoll ? TargetChangeReason.ColdStart : TargetChangeReason.Detected);
            }

            return;
        }

        if (_focusProcessId != _proposed
            && Detected(_focusProcessId) is { } focused
            && _time.GetElapsedTime(_focusSince, now) >= FocusToSwitch
            && !Outranks(_proposed, focused.ProcessId))
        {
            Propose(focused, TargetChangeReason.Focus);
        }
    }

    // Only another listed game takes the Target from a listed game by focus.
    private bool Outranks(int? target, int focused) =>
        target is { } processId
        && _seen.TryGetValue(processId, out var current)
        && current.Candidate is not null
        && IsListed(current)
        && !(_seen.TryGetValue(focused, out var other) && IsListed(other));

    private bool IsListed(SeenProcess game) => IsListed(game, _listedNow);

    // A listed path is the program's own path, its Game Profile's (a Store app's older version), or the raw one.
    private static bool IsListed(SeenProcess game, IReadOnlySet<string> listed) =>
        (game.Path is { } path && listed.Contains(path))
        || (game.ProfileId is { } id && listed.Contains(id))
        || listed.Contains(game.RawPath);

    private TargetCandidate? Detected(int? processId) =>
        processId is { } detected && _seen.TryGetValue(detected, out var seen) ? seen.Candidate : null;

    // The most recently focused detected game, then the most recently started; null when none is detected.
    private TargetCandidate? Best() => _seen.Values
        .Where(seen => seen.Candidate is not null)
        .OrderByDescending(seen => seen.LastFocused)
        .ThenByDescending(seen => seen.Process.StartTime)
        .FirstOrDefault()?.Candidate;

    // Hands over to the best other game when the Target is gone; a lost held switch re-proposes the committed Target.
    private void HandOver(int? committed, int? desired, bool committedGone, bool desiredGone, TargetChangeReason reason)
    {
        if (committedGone)
        {
            Propose(Best(), reason);
        }
        else if (desiredGone && desired != committed)
        {
            Propose(committed is null ? Best() : Detected(committed), reason);
        }
    }

    private void Propose(TargetCandidate? candidate, TargetChangeReason reason)
    {
        _proposed = candidate?.ProcessId;
        _proposedReason = reason;
        if (candidate is null)
        {
            _log.LogInformation("Proposing no Target Game ({Reason})", reason);
        }
        else
        {
            _log.LogInformation(
                "Proposing {Name} (process {Pid}, window 0x{Window:X}) as the Target Game ({Reason})",
                candidate.Name,
                candidate.ProcessId,
                candidate.Window,
                reason);
        }

        _propose(candidate, reason);
    }
}
