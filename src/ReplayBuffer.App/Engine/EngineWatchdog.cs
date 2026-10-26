using System.Collections.Concurrent;
using ReplayBuffer.Contracts;

namespace ReplayBuffer.App.Engine;

/// <summary>
/// Decides when a connected engine is hung (AD-16): no event for <see cref="Supervision.SilenceLimit"/>
/// (3 missed heartbeats; any event counts), or a command with no reply for <see cref="Supervision.ReplyLimit"/>.
/// It only keeps time; the connection acts on <see cref="Check"/>. Thread-safe.
/// </summary>
internal sealed class EngineWatchdog
{
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<long, long> _pending = new();
    private long _lastEvent;
    private long _nextCommand;

    /// <summary>Starts counting from now, as the snapshot that opened the connection is an event.</summary>
    public EngineWatchdog(TimeProvider time)
    {
        _time = time;
        _lastEvent = time.GetTimestamp();
    }

    /// <summary>Records an event from the engine.</summary>
    public void EventReceived() => Interlocked.Exchange(ref _lastEvent, _time.GetTimestamp());

    /// <summary>Records a command sent; pass the result to <see cref="CommandEnded"/> once its reply, or failure, arrives.</summary>
    public long CommandStarted()
    {
        var id = Interlocked.Increment(ref _nextCommand);
        _pending[id] = _time.GetTimestamp();
        return id;
    }

    public void CommandEnded(long id) => _pending.TryRemove(id, out _);

    /// <summary>Why the engine counts as hung, or <c>null</c> while it doesn't.</summary>
    public string? Check()
    {
        var now = _time.GetTimestamp();
        var silent = _time.GetElapsedTime(Interlocked.Read(ref _lastEvent), now);
        if (silent >= Supervision.SilenceLimit)
        {
            return $"The engine sent nothing for {silent.TotalSeconds:0.#} s.";
        }

        foreach (var started in _pending.Values)
        {
            var waiting = _time.GetElapsedTime(started, now);
            if (waiting >= Supervision.ReplyLimit)
            {
                return $"A command had no reply for {waiting.TotalSeconds:0.#} s.";
            }
        }

        return null;
    }
}
