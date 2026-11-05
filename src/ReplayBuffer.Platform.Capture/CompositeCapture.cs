using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ReplayBuffer.Platform.Capture;

/// <summary>
/// Spike 11.1 (AD-25): several monitors captured as one source. Each monitor has its own capture (a
/// WGC monitor item, else, or with <c>duplication</c>, Desktop Duplication) on the one device; each
/// frame it delivers is copied into its place in one BGRA canvas laid out by virtual-desktop
/// position, and the whole canvas is handed on as one frame, so the session's pacer, scaler and one
/// encoder chain see a single source. The canvas never changes size. Per-source arrival statistics
/// are logged at <see cref="Dispose"/> and kept in <see cref="Sources"/>.
/// </summary>
public sealed class CompositeCapture : ICaptureSource
{
    private readonly D3D11Device _device;
    private readonly CapturedFrameHandler _onFrame;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly CompositeSource[] _sources;
    private readonly int _fps;
    private nint _canvas;
    private long _lastQpc;
    private int _open;
    private bool _disposed;

    /// <param name="duplication">Captures every monitor through Desktop Duplication instead of WGC.</param>
    public CompositeCapture(D3D11Device device, IReadOnlyList<MonitorPlacement> placements, int fps, bool duplication, CapturedFrameHandler onFrame, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfZero(placements.Count);
        _device = device;
        _onFrame = onFrame;
        _logger = logger;
        _fps = fps;

        var (size, left, top) = MonitorCanvas.Bounds(placements);
        CanvasSize = size;
        _canvas = device.CreateTexture(size);
        if (_canvas == 0)
        {
            throw new InvalidOperationException($"Could not create the {size} canvas texture.");
        }

        device.Clear(_canvas, 0, 0f, 0f, 0f);
        _sources = [.. placements.Select((p, i) => new CompositeSource(this, i, p, p.X - left, p.Y - top, duplication))];
        _open = _sources.Length;
        logger.LogInformation("Compositing {Count} monitor(s) into one {Size} canvas: {Monitors}",
            _sources.Length, size, string.Join("; ", placements));
    }

    /// <summary>A source's monitor is gone; raised once every source is closed.</summary>
    public event Action? Closed;

    public event Action<Exception>? Failed;

    /// <summary>The canvas: the bounding box of the monitors, in virtual-desktop pixels.</summary>
    public VideoSize CanvasSize { get; }

    /// <summary>Each monitor's source, in placement order.</summary>
    public IReadOnlyList<CompositeSource> Sources => _sources;

    public void Start()
    {
        foreach (var source in _sources)
        {
            source.Start();
        }
    }

    public void SetFps(int fps)
    {
        foreach (var source in _sources)
        {
            source.Capture?.SetFps(fps);
        }
    }

    /// <summary>Stops every source, waiting for frames in flight, logs each source's arrivals and releases the canvas.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        foreach (var source in _sources)
        {
            source.Capture?.Dispose();
        }

        foreach (var source in _sources)
        {
            _logger.LogInformation("Composite source {Summary}", source.Summary());
        }

        D3D11Device.ReleaseTexture(_canvas);
        _canvas = 0;
    }

    private void OnSourceFrame(CompositeSource source, long qpc, nint texture, VideoSize contentSize)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            source.Record(qpc);
            _device.CopyFrameTo(texture, contentSize, _canvas, source.CanvasX, source.CanvasY, source.Area);

            // Two sources' QPCs interleave; the pacer takes them in order.
            _lastQpc = Math.Max(_lastQpc, qpc);
            _onFrame(_lastQpc, _canvas, CanvasSize);
        }
    }

    private void OnSourceClosed(CompositeSource source)
    {
        _logger.LogWarning("Composite source {Device} closed", source.Placement.Monitor.DeviceName);
        if (Interlocked.Decrement(ref _open) == 0)
        {
            Closed?.Invoke();
        }
    }

    /// <summary>One monitor of a <see cref="CompositeCapture"/>: its capture, its place and its arrival statistics.</summary>
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
            for (var i = 0; i < _intervals.Length; i++)
            {
                seen += _intervals[i];
                if (seen >= rank)
                {
                    return (i + 1) * BucketMs;
                }
            }

            return MaxGapMs;
        }

        public string Summary() =>
            FormattableString.Invariant(
                $"{Index} {Placement} via {Api}: {Frames} frames, {Fps:0.0} fps, interval p50 {IntervalPercentileMs(50):0.00} ms, p99 {IntervalPercentileMs(99):0.00} ms, max {MaxGapMs:0.0} ms, {LateFrames} late (> 2 slots)");

        internal void Start()
        {
            var handle = Placement.Monitor.Handle;
            if (!_duplication)
            {
                ICaptureSource? wgc = null;
                try
                {
                    wgc = WindowCapture.ForMonitor(_owner._device, handle, _owner._fps, OnFrame, _owner._logger);
                    Wire(wgc);
                    wgc.Start();
                    Api = "wgc";
                    return;
                }
                catch (Exception e)
                {
                    wgc?.Dispose();
                    Capture = null;
                    _owner._logger.LogWarning("WGC could not capture {Device} ({Message}); falling back to Desktop Duplication", Placement.Monitor.DeviceName, e.Message);
                }
            }

            var duplication = new DesktopDuplicationCapture(_owner._device, handle, _owner._fps, OnFrame, _owner._logger);
            Wire(duplication);
            duplication.Start();
            Api = "desktop-duplication";
        }

        internal void Record(long qpc)
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
                if (ms > 2000.0 / _owner._fps)
                {
                    _lateFrames++;
                }
            }

            _last = qpc;
        }

        private void Wire(ICaptureSource capture)
        {
            Capture = capture;
            capture.Closed += () => _owner.OnSourceClosed(this);
            capture.Failed += e => _owner.Failed?.Invoke(e);
        }

        private void OnFrame(long qpc, nint texture, VideoSize contentSize) => _owner.OnSourceFrame(this, qpc, texture, contentSize);
    }
}
