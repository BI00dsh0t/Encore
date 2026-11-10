namespace Encore.Media;

/// <summary>The constant-frame-rate pacer: exactly one frame per slot of the output fps. Rules: docs/code-notes.md.</summary>
public sealed class FramePacer
{
    public const int MaxSlotsBehind = 4;

    private readonly long _qpcFrequency;
    private readonly long _timeBase;
    private readonly bool _firstFrameTakesNearestSlot;
    private long _newestQpc = long.MinValue;
    private long _heldQpc;
    private bool _running;

    /// <summary>With firstFrameTakesNearestSlot, a chain that starts mid-session gets no repeats for the slots before it.</summary>
    public FramePacer(long qpcFrequency, int fps, long anchorQpc, long timeBase = 90_000, bool firstFrameTakesNearestSlot = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBase, fps);
        _qpcFrequency = qpcFrequency;
        _timeBase = timeBase;
        _firstFrameTakesNearestSlot = firstFrameTakesNearestSlot;
        Fps = fps;
        AnchorQpc = anchorQpc;
    }

    public int Fps { get; }

    public long AnchorQpc { get; }

    /// <summary>The lowest slot not yet encoded.</summary>
    public long NextSlot { get; private set; }

    /// <summary>A frame is held for <see cref="NextSlot"/>.</summary>
    public bool HasHeld { get; private set; }

    /// <summary>Slots encoded with a new frame.</summary>
    public long FreshSlots { get; private set; }

    /// <summary>Slots encoded with the last frame again.</summary>
    public long RepeatedSlots { get; private set; }

    /// <summary>Slots jumped over because the encoder fell behind.</summary>
    public long SkippedSlots { get; private set; }

    /// <summary>Held frames superseded by a newer one before their slot.</summary>
    public long HeldReplaced { get; private set; }

    /// <summary>Frames dropped because their capture QPC was not after a frame already taken.</summary>
    public long StaleFrames { get; private set; }

    /// <summary>Slots encoded, new or repeated.</summary>
    public long EmittedSlots => FreshSlots + RepeatedSlots;

    /// <summary>The QPC instant of slot <paramref name="slot"/>.</summary>
    public long SlotQpc(long slot) => AnchorQpc + (long)((Int128)slot * _qpcFrequency / Fps);

    /// <summary>The PTS of slot <paramref name="slot"/>, in 1/timeBase.</summary>
    public long Pts(long slot) => (long)((Int128)slot * _timeBase / Fps);

    /// <summary>The slot whose instant is nearest <paramref name="qpc"/> (halfway rounds up).</summary>
    public long NearestSlot(long qpc) => FloorDiv(((Int128)(qpc - AnchorQpc) * Fps * 2) + _qpcFrequency, (Int128)_qpcFrequency * 2);

    /// <summary>The slot whose span holds <paramref name="qpc"/>.</summary>
    public long CurrentSlot(long qpc) => FloorDiv((Int128)(qpc - AnchorQpc) * Fps, _qpcFrequency);

    /// <summary>When <see cref="Poll"/> next has work: the held frame's slot time, else the next slot's time + ½ slot.</summary>
    public long NextDeadline => HasHeld ? SlotQpc(NextSlot) : RepeatQpc(NextSlot);

    /// <summary>Offers a new frame captured at <paramref name="captureQpc"/>; call after <see cref="Poll"/> has caught up.</summary>
    public PacerDecision Offer(long captureQpc)
    {
        if (captureQpc <= _newestQpc)
        {
            StaleFrames++;
            return new PacerDecision(PacerAction.Drop, NextSlot, Pts(NextSlot));
        }

        if (_firstFrameTakesNearestSlot && EmittedSlots == 0 && !HasHeld)
        {
            NextSlot = Math.Max(NextSlot, NearestSlot(captureQpc));
        }

        _newestQpc = captureQpc;
        var replaced = HasHeld;
        if (replaced)
        {
            HeldReplaced++;
        }

        if (NearestSlot(captureQpc) >= NextSlot)
        {
            // Its nearest slot is free (a later one is impossible once Poll has caught up).
            HasHeld = false;
            var slot = NextSlot++;
            FreshSlots++;
            return new PacerDecision(PacerAction.Emit, slot, Pts(slot), replaced);
        }

        HasHeld = true;
        _heldQpc = captureQpc;
        return new PacerDecision(PacerAction.Hold, NextSlot, Pts(NextSlot), replaced);
    }

    /// <summary>The capture QPC of the held frame; meaningful only while <see cref="HasHeld"/>.</summary>
    public long HeldQpc => _heldQpc;

    /// <summary>The next piece of time-driven work at <paramref name="nowQpc"/>, or <see cref="PacerAction.None"/>.</summary>
    public PacerDecision Poll(long nowQpc)
    {
        var behind = CurrentSlot(nowQpc) - NextSlot;
        if (_running && behind > MaxSlotsBehind)
        {
            SkippedSlots += behind;
            NextSlot += behind;
        }

        if (HasHeld)
        {
            if (nowQpc < SlotQpc(NextSlot))
            {
                _running = true;
                return default;
            }

            HasHeld = false;
            var slot = NextSlot++;
            FreshSlots++;
            return new PacerDecision(PacerAction.EmitHeld, slot, Pts(slot));
        }

        if (nowQpc < RepeatQpc(NextSlot))
        {
            _running = true; // caught up: from now on, falling behind is overload
            return default;
        }

        var repeat = NextSlot++;
        RepeatedSlots++;
        return new PacerDecision(PacerAction.Repeat, repeat, Pts(repeat));
    }

    /// <summary>Slot <paramref name="slot"/>'s time + ½ slot: with no new frame by then, it repeats.</summary>
    private long RepeatQpc(long slot) => AnchorQpc + (long)(((Int128)slot * 2 + 1) * _qpcFrequency / (Fps * 2));

    private static long FloorDiv(Int128 numerator, Int128 denominator)
    {
        var quotient = numerator / denominator;
        return (long)(numerator % denominator < 0 ? quotient - 1 : quotient);
    }
}
