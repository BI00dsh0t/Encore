namespace Encore.Media.Tests;

/// <summary>Story 1.11's pacer: slot PTS, hold, replace, repeat and skip, driven the way the encoder thread drives it.</summary>
public sealed class FramePacerTests
{
    private const long Qpf = 12_000_000; // divisible by 2 × every tested fps
    private const long Anchor = 5_000_000_000;

    public static TheoryData<int> FrameRates => [30, 50, 60, 120];

    [Theory]
    [InlineData(30, 3000)]
    [InlineData(50, 1800)]
    public void SlotPtsStepByExactlyOneSlotIn90kHz(int fps, long step)
    {
        var pacer = new FramePacer(Qpf, fps, Anchor);
        for (long n = 0; n < 10 * fps; n++)
        {
            Assert.Equal(n * step, pacer.Pts(n));
            Assert.Equal(Anchor + (n * Qpf / fps), pacer.SlotQpc(n));
        }
    }

    [Fact]
    public void ANonDividingRateStillGivesStrictlyIncreasingPts()
    {
        var pacer = new FramePacer(Qpf, 7, Anchor);
        for (long n = 1; n < 100; n++)
        {
            Assert.True(pacer.Pts(n) > pacer.Pts(n - 1));
        }
    }

    [Theory]
    [MemberData(nameof(FrameRates))]
    public void AStaticWindowGetsEverySlotByRepeatingItsOneFrame(int fps)
    {
        var pacer = new FramePacer(Qpf, fps, Anchor);
        var run = Simulate(pacer, [Anchor], Anchor + (5 * Qpf));

        Assert.Equal(5 * fps, run.Count);
        Assert.Equal(PacerAction.Emit, run[0].Action);
        Assert.All(run.Skip(1), d => Assert.Equal(PacerAction.Repeat, d.Action));
        AssertUniform(pacer, run);
        Assert.Equal(1, pacer.FreshSlots);
        Assert.Equal((5 * fps) - 1, pacer.RepeatedSlots);
    }

    [Fact]
    public void AFrameStarvedSourceGetsEverySlotForAMinuteWithNoPtsBreak()
    {
        // The secure desktop (T-04): one frame, then none for 60 s.
        const int fps = 60;
        var pacer = new FramePacer(Qpf, fps, Anchor);
        var run = Simulate(pacer, [Anchor], Anchor + (60 * Qpf) + (Qpf / fps));

        Assert.Equal(PacerAction.Emit, run[0].Action);
        var after = run.Skip(1).ToList();
        Assert.Equal(60 * fps, after.Count);
        Assert.All(after, d => Assert.Equal(PacerAction.Repeat, d.Action));
        AssertUniform(pacer, run);
    }

    [Theory]
    [MemberData(nameof(FrameRates))]
    public void AWindowRepaintingAtTwiceTheRateFillsEverySlotWithANewFrame(int fps)
    {
        var pacer = new FramePacer(Qpf, fps, Anchor);

        // Frames every ½ slot, with ±15 % of a half slot of jitter.
        var random = new Random(fps);
        var half = Qpf / fps / 2;
        var frames = new List<long>();
        for (var i = 0; i < 2 * 5 * fps; i++)
        {
            frames.Add(Anchor + (i * half) + (i == 0 ? 0 : (long)((random.NextDouble() - 0.5) * 0.3 * half)));
        }

        var run = Simulate(pacer, frames, Anchor + (5 * Qpf));

        AssertUniform(pacer, run);
        Assert.True(pacer.FreshSlots >= 0.95 * pacer.EmittedSlots, $"{pacer.FreshSlots} of {pacer.EmittedSlots} slots fresh");
        Assert.Equal(0, pacer.SkippedSlots);
    }

    [Fact]
    public void AGameAt78FpsFillsEvery60FpsSlotWithANewFrame()
    {
        var pacer = new FramePacer(Qpf, 60, Anchor);
        var frames = Enumerable.Range(0, 78 * 5).Select(i => Anchor + (i * Qpf / 78)).ToList();

        var run = Simulate(pacer, frames, Anchor + (5 * Qpf));

        AssertUniform(pacer, run);
        Assert.Equal(pacer.EmittedSlots, pacer.FreshSlots);
        Assert.True(pacer.HeldReplaced > 0 || pacer.EmittedSlots < frames.Count);
    }

    [Fact]
    public void ANewFrameTakesItsNearestSlotAtOnceWhenItIsFree()
    {
        var pacer = new FramePacer(Qpf, 60, Anchor);
        var slot = Qpf / 60;
        Assert.Equal(new PacerDecision(PacerAction.Emit, 0, 0), pacer.Offer(Anchor));

        // 0.6 slot in: slot 1 is nearest, and free, so it's encoded now, ahead of its time.
        var at = Anchor + (slot * 6 / 10);
        Assert.Equal(PacerAction.None, pacer.Poll(at).Action);
        Assert.Equal(new PacerDecision(PacerAction.Emit, 1, 1500), pacer.Offer(at));
        Assert.Equal(2, pacer.NextSlot);
    }

    [Fact]
    public void AFrameWhoseSlotIsTakenIsHeldForTheNextSlotAndANewerOneReplacesIt()
    {
        var pacer = new FramePacer(Qpf, 60, Anchor);
        var slot = Qpf / 60;
        pacer.Offer(Anchor);
        pacer.Offer(Anchor + (slot * 6 / 10)); // slot 1, at once

        // 1.2 slots: nearest is slot 1, taken → held for slot 2.
        var held = pacer.Offer(Anchor + (slot * 12 / 10));
        Assert.Equal(new PacerDecision(PacerAction.Hold, 2, 3000), held);
        Assert.True(pacer.HasHeld);
        Assert.Equal(pacer.SlotQpc(2), pacer.NextDeadline);

        // 1.4 slots: also slot 1's → replaces the held frame.
        var newer = pacer.Offer(Anchor + (slot * 14 / 10));
        Assert.Equal(new PacerDecision(PacerAction.Hold, 2, 3000, ReplacedHeld: true), newer);
        Assert.Equal(1, pacer.HeldReplaced);

        // At slot 2's time the held frame goes out.
        Assert.Equal(PacerAction.None, pacer.Poll(pacer.SlotQpc(2) - 1).Action);
        Assert.Equal(new PacerDecision(PacerAction.EmitHeld, 2, 3000), pacer.Poll(pacer.SlotQpc(2)));
        Assert.False(pacer.HasHeld);
        Assert.Equal(3, pacer.FreshSlots);
    }

    [Fact]
    public void AFrameForTheHeldSlotReplacesTheHeldFrameAndGoesOutAtOnce()
    {
        var pacer = new FramePacer(Qpf, 60, Anchor);
        var slot = Qpf / 60;
        pacer.Offer(Anchor);
        pacer.Offer(Anchor + (slot * 3 / 10)); // held for slot 1

        var replacing = pacer.Offer(Anchor + (slot * 6 / 10));
        Assert.Equal(new PacerDecision(PacerAction.Emit, 1, 1500, ReplacedHeld: true), replacing);
        Assert.False(pacer.HasHeld);
    }

    [Fact]
    public void ASlotWithNoNewFrameRepeatsHalfASlotAfterItsTime()
    {
        var pacer = new FramePacer(Qpf, 60, Anchor);
        pacer.Offer(Anchor);
        var due = pacer.SlotQpc(1) + (Qpf / 120);

        Assert.Equal(due, pacer.NextDeadline);
        Assert.Equal(PacerAction.None, pacer.Poll(due - 1).Action);
        Assert.Equal(new PacerDecision(PacerAction.Repeat, 1, 1500), pacer.Poll(due));
        Assert.Equal(PacerAction.None, pacer.Poll(due).Action);
    }

    [Fact]
    public void FallingMoreThanFourSlotsBehindJumpsToTheCurrentSlot()
    {
        var pacer = new FramePacer(Qpf, 60, Anchor);
        pacer.Offer(Anchor);
        Assert.Equal(PacerAction.None, pacer.Poll(Anchor).Action); // caught up: running

        // 4 behind is caught up by repeats; 5 behind is a jump.
        Assert.Equal(new PacerDecision(PacerAction.Repeat, 1, 1500), pacer.Poll(pacer.SlotQpc(5)));
        Assert.Equal(0, pacer.SkippedSlots);

        var late = pacer.SlotQpc(20) + 1;
        var first = pacer.Poll(late);
        Assert.Equal(18, pacer.SkippedSlots); // slots 2..19
        Assert.Equal(20, pacer.NextSlot);
        Assert.Equal(PacerAction.None, first.Action); // slot 20's repeat is due at 20.5

        var after = pacer.Poll(pacer.SlotQpc(20) + (Qpf / 120));
        Assert.Equal(new PacerDecision(PacerAction.Repeat, 20, 20 * 1500), after);
    }

    [Fact]
    public void ALateAnchorIsFilledWithRepeatsNotSkipped()
    {
        // The anchor frame reaches the encoder thread 8 slots late at 120 fps (the output opening).
        var pacer = new FramePacer(Qpf, 120, Anchor);
        Assert.Equal(PacerAction.Emit, pacer.Offer(Anchor).Action);

        var late = pacer.SlotQpc(8);
        var repeats = new List<long>();
        for (var d = pacer.Poll(late); d.Action != PacerAction.None; d = pacer.Poll(late))
        {
            Assert.Equal(PacerAction.Repeat, d.Action);
            repeats.Add(d.Slot);
        }

        Assert.Equal([1L, 2, 3, 4, 5, 6, 7], repeats);
        Assert.Equal(0, pacer.SkippedSlots);

        // Once caught up, the same lateness is overload.
        pacer.Poll(pacer.SlotQpc(20));
        Assert.Equal(12, pacer.SkippedSlots);
    }

    [Fact]
    public void AnOverloadedEncoderKeepsPtsStrictlyIncreasing()
    {
        var pacer = new FramePacer(Qpf, 60, Anchor);
        var slot = Qpf / 60;
        var emitted = new List<long>();
        long now = Anchor;
        Record(pacer.Offer(now));

        // The encoder thread wakes only every 7 slots; frames keep arriving.
        for (var wake = 1; wake < 40; wake++)
        {
            now = Anchor + (wake * 7 * slot) + (slot / 3);
            Drain(now);
            Record(pacer.Offer(now - (slot / 10)));
        }

        Assert.True(pacer.SkippedSlots > 0);
        for (var i = 1; i < emitted.Count; i++)
        {
            Assert.True(emitted[i] > emitted[i - 1]);
        }

        void Drain(long at)
        {
            for (var d = pacer.Poll(at); d.Action != PacerAction.None; d = pacer.Poll(at))
            {
                Record(d);
            }
        }

        void Record(PacerDecision d)
        {
            if (d.Action is PacerAction.Emit or PacerAction.EmitHeld or PacerAction.Repeat)
            {
                emitted.Add(d.Pts);
            }
        }
    }

    [Fact]
    public void AFrameNotAfterTheNewestIsDropped()
    {
        var pacer = new FramePacer(Qpf, 60, Anchor);
        pacer.Offer(Anchor + 100);
        Assert.Equal(PacerAction.Drop, pacer.Offer(Anchor + 100).Action);
        Assert.Equal(PacerAction.Drop, pacer.Offer(Anchor).Action);
        Assert.Equal(2, pacer.StaleFrames);
        Assert.Equal(1, pacer.EmittedSlots);
    }

    [Fact]
    public void AResizeOrModeChangeGapRepeatsUntilFramesReturnWithNoPtsBreak()
    {
        // 1 s of frames at 60, 400 ms with none (WGC recreating its pool), then frames again.
        var pacer = new FramePacer(Qpf, 60, Anchor);
        var frames = Enumerable.Range(0, 60).Select(i => Anchor + (i * Qpf / 60))
            .Concat(Enumerable.Range(0, 60).Select(i => Anchor + (14 * Qpf / 10) + (i * Qpf / 60)))
            .ToList();

        var run = Simulate(pacer, frames, Anchor + (24 * Qpf / 10));

        AssertUniform(pacer, run);
        Assert.Equal(144, run.Count);
        Assert.InRange(pacer.RepeatedSlots, 23, 25);
    }

    [Fact]
    public void AChainStartedMidSessionGivesItsFirstFrameTheNearestSlotWithNoRepeatsBeforeIt()
    {
        var pacer = new FramePacer(Qpf, 30, Anchor, firstFrameTakesNearestSlot: true);
        var first = Anchor + (5 * Qpf);

        var decision = pacer.Offer(first);

        Assert.Equal((PacerAction.Emit, 150L, pacer.Pts(150)), (decision.Action, decision.Slot, decision.Pts));
        Assert.Equal(PacerAction.None, pacer.Poll(first).Action);
        var repeat = pacer.Poll(pacer.SlotQpc(151) + (Qpf / 60));
        Assert.Equal((PacerAction.Repeat, 151L), (repeat.Action, repeat.Slot));
        Assert.Equal(1, pacer.RepeatedSlots);
    }

    /// <summary>
    /// Drives the pacer as the encoder thread does: it wakes at each frame's arrival (its capture
    /// QPC) and at each deadline, polls until nothing is due, then offers the frame.
    /// </summary>
    private static List<PacerDecision> Simulate(FramePacer pacer, IReadOnlyList<long> frames, long end)
    {
        var emitted = new List<PacerDecision>();
        var next = 0;
        while (true)
        {
            var deadline = next == 0 ? long.MaxValue : pacer.NextDeadline;
            var arrival = next < frames.Count ? frames[next] : long.MaxValue;
            var now = Math.Min(deadline, arrival);
            if (now >= end)
            {
                break;
            }

            for (var d = pacer.Poll(now); d.Action != PacerAction.None; d = pacer.Poll(now))
            {
                emitted.Add(d);
            }

            if (arrival == now)
            {
                var decision = pacer.Offer(frames[next++]);
                if (decision.Action == PacerAction.Emit)
                {
                    emitted.Add(decision);
                }
            }
        }

        return emitted;
    }

    private static void AssertUniform(FramePacer pacer, List<PacerDecision> run)
    {
        for (var i = 0; i < run.Count; i++)
        {
            Assert.Equal(i, run[i].Slot);
            Assert.Equal(pacer.Pts(i), run[i].Pts);
        }
    }
}
