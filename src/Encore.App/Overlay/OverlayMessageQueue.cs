using Encore.Contracts.Messages;

namespace Encore.App.Overlay;

/// <summary>The Overlay's messages: each new Overlay warning waits its turn and shows for a few seconds. UI thread.</summary>
internal sealed class OverlayMessageQueue
{
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly Action _expired;
    private readonly List<Warning> _waiting = [];
    private readonly HashSet<string> _seen = [];
    private ITimer? _messageEnd;
    private Warning? _shown;

    /// <summary>Post runs on the UI thread, where expired is called when a message's time is up.</summary>
    public OverlayMessageQueue(TimeProvider time, Action<Action> post, Action expired)
    {
        _time = time;
        _post = post;
        _expired = expired;
    }

    /// <summary>Queues the unseen Overlay warnings and picks the one to show; null while disconnected.</summary>
    public Warning? Next(StateSnapshot? state)
    {
        if (state is null)
        {
            // What was showing has been seen; what was waiting still waits.
            End();
            return null;
        }

        var active = state.Warnings.Where(warning => warning.Overlay).ToList();
        var activeIds = active.Select(warning => warning.Id).ToHashSet();
        _seen.RemoveWhere(id => !activeIds.Contains(id));
        foreach (var warning in active.OrderBy(warning => warning.At))
        {
            if (_seen.Add(warning.Id))
            {
                Enqueue(warning);
            }
        }

        // Dismissed before their turn, or while showing.
        _waiting.RemoveAll(warning => !activeIds.Contains(warning.Id));
        if (_shown is not null && !activeIds.Contains(_shown.Id))
        {
            End();
        }

        if (_shown is null && _waiting.Count > 0)
        {
            ShowNext();
        }

        return _shown;
    }

    // A repeat of a waiting message takes its place; a repeat of the one on screen queues.
    private void Enqueue(Warning warning)
    {
        var repeat = _waiting.FindIndex(waiting => waiting.Code == warning.Code && waiting.Message == warning.Message);
        if (repeat >= 0)
        {
            _waiting[repeat] = warning;
        }
        else
        {
            _waiting.Add(warning);
        }
    }

    private void ShowNext()
    {
        var shown = _waiting[0];
        _waiting.RemoveAt(0);
        _shown = shown;
        _messageEnd = _time.CreateTimer(_ => _post(() => Expire(shown)), null, OverlayViewModel.MessageDuration, Timeout.InfiniteTimeSpan);
    }

    // A timer already queued when its message ended early must not end the next one.
    private void Expire(Warning shown)
    {
        if (ReferenceEquals(_shown, shown))
        {
            End();
            _expired();
        }
    }

    private void End()
    {
        _messageEnd?.Dispose();
        _messageEnd = null;
        _shown = null;
    }
}
