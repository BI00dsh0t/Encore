using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Encore.Platform.Capture;

public sealed partial class CompositeCapture
{
    /// <summary>One screen of a <see cref="CompositeCapture"/>: its capture, its place and its arrival statistics.</summary>
    public sealed class CompositeSource
    {
        private const double BucketMs = 0.25;
        private const int Buckets = 800; // 0 to 200 ms

        private readonly CompositeCapture _owner;
        private readonly bool _duplication;
        private readonly long[] _intervals = new long[Buckets + 1];
        private readonly double _qpcPerMs = Stopwatch.Frequency / 1000.0;
        private long _first;
        private long _last;
        private long _maxGap;
        private long _lateFrames;
        private int _removed;

        internal CompositeSource(CompositeCapture owner, int index, MonitorPlacement placement, int x, int y, bool duplication)
        {
            _owner = owner;
            Index = index;
            Placement = placement;
            CanvasX = x;
            CanvasY = y;
            Area = new VideoSize(placement.Width, placement.Height);
            _duplication = duplication;
        }

        public int Index { get; }

        public MonitorPlacement Placement { get; }

        public int CanvasX { get; }

        public int CanvasY { get; }

        public VideoSize Area { get; }

        /// <summary><c>wgc</c> or <c>desktop-duplication</c>, once started.</summary>
        public string Api { get; private set; } = "";

        public long Frames { get; private set; }

        public bool Removed => Volatile.Read(ref _removed) == 1;

        internal ICaptureSource? Capture { get; private set; }

        /// <summary>Frames per second between the first and last arrival.</summary>
        public double Fps => Frames > 1 ? (Frames - 1) * _qpcPerMs * 1000 / (_last - _first) : 0;

        public double MaxGapMs => _maxGap / _qpcPerMs;

        /// <summary>Arrivals more than two output slots after the previous one.</summary>
        public long LateFrames => _lateFrames;

        /// <summary>The <paramref name="percentile"/> of the arrival intervals, in ms (to a 0.25 ms bucket).</summary>
        public double IntervalPercentileMs(double percentile)
        {
            var total = Frames - 1;
            if (total <= 0)
            {
                return 0;
            }

            var rank = (long)Math.Ceiling(total * percentile / 100);
            long seen = 0;
            for (var bucket = 0; bucket < _intervals.Length; bucket++)
            {
                seen += _intervals[bucket];
                if (seen >= rank)
                {
                    return (bucket + 1) * BucketMs;
                }
            }

            return MaxGapMs;
        }

        public string Summary() =>
            FormattableString.Invariant(
                $"{Index} {Placement} via {Api}: {Frames} frames, {Fps:0.0} fps, ")
            + FormattableString.Invariant(
                $"interval p50 {IntervalPercentileMs(50):0.00} ms, p99 {IntervalPercentileMs(99):0.00} ms, max {MaxGapMs:0.0} ms, ")
            + FormattableString.Invariant($"{LateFrames} late (> 2 slots){(Removed ? ", removed" : "")}");

        internal bool MarkRemoved() => Interlocked.Exchange(ref _removed, 1) == 0;

        internal void Start()
        {
            if (_owner._sourceFactory is { } factory)
            {
                var made = factory(this, OnFrame);
                Wire(made);
                made.Start();
                Api = "test";
                return;
            }

            if (!_duplication && TryStartWgc())
            {
                return;
            }

            var device = _owner._device!;
            var duplication = new DesktopDuplicationCapture(
                device, Placement.Monitor.Handle, Volatile.Read(ref _owner._fps), OnFrame, _owner.Logger);
            Wire(duplication);
            duplication.Start();
            Api = "desktop-duplication";
        }

        // WGC first; a screen it can't capture falls back to Desktop Duplication.
        private bool TryStartWgc()
        {
            ICaptureSource? wgc = null;
            try
            {
                wgc = WindowCapture.ForMonitor(
                    _owner._device!, Placement.Monitor.Handle, Volatile.Read(ref _owner._fps), OnFrame, _owner.Logger);
                Wire(wgc);
                wgc.Start();
                Api = "wgc";
                return true;
            }
            catch (Exception e)
            {
                wgc?.Dispose();
                Capture = null;
                _owner.Logger.LogWarning(
                    "WGC could not capture {Device} ({Message}); falling back to Desktop Duplication",
                    Placement.Monitor.DeviceName,
                    e.Message);
                return false;
            }
        }

        private void Record(long qpc)
        {
            if (Frames++ == 0)
            {
                _first = qpc;
            }
            else
            {
                var gap = qpc - _last;
                _maxGap = Math.Max(_maxGap, gap);
                var ms = gap / _qpcPerMs;
                _intervals[Math.Clamp((int)(ms / BucketMs), 0, Buckets)]++;
                if (ms > 2000.0 / Volatile.Read(ref _owner._fps))
                {
                    _lateFrames++;
                }
            }

            _last = qpc;
        }

        private void Wire(ICaptureSource capture)
        {
            Capture = capture;
            capture.Closed += () => _owner.RemoveSource(this, "closed");
            capture.Failed += error => _owner.RaiseFailed(error);
        }

        // One source's frames arrive one at a time on its own thread.
        private void OnFrame(long qpc, nint texture, VideoSize contentSize)
        {
            if (Removed)
            {
                return;
            }

            Record(qpc);
            _owner._compositor.OnFrame(Index, texture, contentSize.Width, contentSize.Height, Stopwatch.GetTimestamp());
        }
    }
}
