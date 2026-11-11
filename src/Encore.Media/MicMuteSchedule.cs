namespace Encore.Media;

/// <summary>A session's mic mute over time: the state at Start, then each change with the QPC it applies from. Thread-safe.</summary>
public sealed class MicMuteSchedule
{
    private readonly Lock _writer = new();

    /// <summary>The changes in QPC order; the first is the Start state, from the beginning of time.</summary>
    private (long Qpc, bool Muted)[] _changes;

    /// <summary>The muted argument is the mute at Start, in place from the first frame.</summary>
    public MicMuteSchedule(bool muted) => _changes = [(long.MinValue, muted)];

    /// <summary>The latest state.</summary>
    public bool Muted => Volatile.Read(ref _changes)[^1].Muted;

    /// <summary>The log, read once per run by a gate.</summary>
    internal (long Qpc, bool Muted)[] Changes => Volatile.Read(ref _changes);

    /// <summary>Sets the mute from a QPC time on; a time earlier than the last change's is taken as that change's.</summary>
    public void Set(bool muted, long qpc)
    {
        lock (_writer)
        {
            var current = _changes;
            var last = current[^1];
            if (last.Muted == muted)
            {
                return;
            }

            var next = new (long Qpc, bool Muted)[current.Length + 1];
            current.CopyTo(next, 0);
            next[^1] = (Math.Max(qpc, last.Qpc), muted);
            Volatile.Write(ref _changes, next);
        }
    }
}
