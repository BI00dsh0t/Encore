using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Media;
using Windows.Win32.Foundation;

namespace Encore.Platform.Capture.Tests;

public sealed class DesktopDuplicationCaptureTests
{
    private const int Fps = 30;
    private static readonly long Qpf = Stopwatch.Frequency;

    /// <summary>A bug that bit: waiting in AcquireNextFrame held the device lock, so the encoder fell behind and left PTS gaps.</summary>
    [Fact]
    public void AStallingDuplicationKeepsTheEncodedSlotsOnThe30FpsGrid()
    {
        var deviceLock = new Lock();
        var start = Stopwatch.GetTimestamp();
        var duplication = new StallingDuplication(deviceLock, stallFrom: start + Qpf / 5, stallTo: start + Qpf);
        var frames = new ConcurrentQueue<long>();
        using var capture = new DesktopDuplicationCapture(duplication, Fps, (qpc, _, _) => frames.Enqueue(qpc), NullLogger.Instance);
        capture.Start();

        long anchor;
        while (!frames.TryDequeue(out anchor))
        {
            Thread.Sleep(1);
        }

        var pacer = new FramePacer(Qpf, Fps, anchor);
        var pts = new List<long>();
        long longestLockWait = 0;
        void Encode(long slotPts)
        {
            var asked = Stopwatch.GetTimestamp();
            lock (deviceLock)
            {
                longestLockWait = Math.Max(longestLockWait, Stopwatch.GetTimestamp() - asked);
                pts.Add(slotPts);
            }
        }

        void CatchUp()
        {
            for (var step = pacer.Poll(Stopwatch.GetTimestamp()); step.Action != PacerAction.None; step = pacer.Poll(Stopwatch.GetTimestamp()))
            {
                Encode(step.Pts);
            }
        }

        void Offer(long qpc)
        {
            var decision = pacer.Offer(qpc);
            if (decision.Action == PacerAction.Emit)
            {
                Encode(decision.Pts);
            }
        }

        Offer(anchor);
        var end = start + Qpf * 6 / 5;
        while (Stopwatch.GetTimestamp() < end)
        {
            while (frames.TryDequeue(out var qpc))
            {
                CatchUp();
                Offer(qpc);
            }

            CatchUp();
            Thread.Sleep(1);
        }

        capture.Dispose();

        var slot = VideoStep();
        for (var i = 1; i < pts.Count; i++)
        {
            Assert.True(pts[i] - pts[i - 1] == slot, $"PTS step {pts[i] - pts[i - 1]} at slot {i}, not {slot}");
        }

        Assert.Equal(0, pacer.SkippedSlots);
        Assert.True(pacer.RepeatedSlots >= 15, $"only {pacer.RepeatedSlots} repeats filled the 0.8 s stall");
        Assert.True(longestLockWait < Qpf / Fps, $"the encoder waited {longestLockWait * 1000.0 / Qpf:0.0} ms for the device lock");

        // Polled every half slot, never spinning on an unchanged desktop.
        Assert.InRange(duplication.Polls, 20, 2 * Fps * 6 / 5 + 10);
    }

    private static long VideoStep() => 90_000 / Fps;

    /// <summary>A desktop that changes on every poll, except between <c>stallFrom</c> and <c>stallTo</c>.</summary>
    private sealed class StallingDuplication(Lock deviceLock, long stallFrom, long stallTo) : IOutputDuplication
    {
        private int _polls;

        public int Polls => Volatile.Read(ref _polls);

        public VideoSize ModeSize => new(64, 64);

        public HRESULT Duplicate() => HRESULT.S_OK;

        public void Release()
        {
        }

        public HRESULT TryAcquire(out DuplicatedFrame frame)
        {
            lock (deviceLock)
            {
                Interlocked.Increment(ref _polls);
                var now = Stopwatch.GetTimestamp();
                if (now >= stallFrom && now < stallTo)
                {
                    frame = default;
                    return HRESULT.DXGI_ERROR_WAIT_TIMEOUT;
                }

                frame = new DuplicatedFrame(now, 1, ModeSize);
                return HRESULT.S_OK;
            }
        }

        public HRESULT ReleaseFrame() => HRESULT.S_OK;
    }
}
