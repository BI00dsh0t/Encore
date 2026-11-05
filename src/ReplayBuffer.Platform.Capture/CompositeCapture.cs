using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace ReplayBuffer.Platform.Capture;

/// <summary>
/// A multi-monitor Monitor Recording's source (AD-25, story 11.3; spike 11.1's base). Each screen has
/// its own capture (a WGC monitor item, else, or with <c>duplication</c>, Desktop Duplication) on the
/// one device. A <see cref="MonitorCompositor"/> draws each frame into that screen's place in one BGRA
/// canvas laid out by virtual-desktop position, and its slot clock, a high-resolution timer thread at
/// the output frame rate, hands the canvas on as one frame per slot. So the session's pacer, scaler and
/// one encoder chain see a single source with increasing stamps; a frame that opens a new slot hands
/// the canvas on at once, from its source's thread. The canvas never changes size: a
/// screen whose mode changes is scaled into its place, and a removed screen's place turns black.
/// <see cref="Closed"/> is raised once no screen is left. Per-source arrival statistics are logged at
/// <see cref="Dispose"/> and kept in <see cref="Sources"/>.
/// </summary>
public sealed unsafe class CompositeCapture : ICaptureSource
{
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;
    private const long HundredNanosecondsPerSecond = 10_000_000;

    private readonly D3D11Device? _device;
    private readonly Func<CompositeSource, CapturedFrameHandler, ICaptureSource>? _sourceFactory;
    private readonly CapturedFrameHandler _onFrame;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly CompositeSource[] _sources;
    private readonly MonitorCompositor _compositor;
    private readonly ICanvasTarget _surface;
    private readonly Thread _clock;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private HANDLE _stopEvent;
    private HANDLE _timer;
    private volatile bool _stopping;
    private int _fps;
    private int _closedRaised;
    private bool _started;
    private bool _clockStarted;
    private bool _disposed;

    /// <param name="duplication">Whether a screen is captured through Desktop Duplication instead of WGC (for tests and <c>--monitor-api dd</c>); null for none.</param>
    public CompositeCapture(D3D11Device device, IReadOnlyList<MonitorPlacement> placements, int fps, Func<MonitorPlacement, bool>? duplication, CapturedFrameHandler onFrame, ILogger logger)
        : this(device ?? throw new ArgumentNullException(nameof(device)), placements, fps, duplication, null, (owner, size) => new CanvasSurface(owner, size), onFrame, logger)
    {
    }

    /// <summary>For tests: each screen's capture comes from <paramref name="sources"/> and the canvas from <paramref name="canvas"/>; no GPU device.</summary>
    internal CompositeCapture(
        IReadOnlyList<MonitorPlacement> placements,
        int fps,
        Func<CompositeSource, CapturedFrameHandler, ICaptureSource> sources,
        Func<VideoSize, ICanvasTarget> canvas,
        CapturedFrameHandler onFrame,
        ILogger logger)
        : this(null, placements, fps, null, sources, (_, size) => canvas(size), onFrame, logger)
    {
    }

    private CompositeCapture(
        D3D11Device? device,
        IReadOnlyList<MonitorPlacement> placements,
        int fps,
        Func<MonitorPlacement, bool>? duplication,
        Func<CompositeSource, CapturedFrameHandler, ICaptureSource>? sources,
        Func<CompositeCapture, VideoSize, ICanvasTarget> canvas,
        CapturedFrameHandler onFrame,
        ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfZero(placements.Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        _device = device;
        _sourceFactory = sources;
        _onFrame = onFrame;
        _logger = logger;
        _fps = fps;

        var (size, left, top) = MonitorCanvas.Bounds(placements);
        CanvasSize = size;
        _surface = canvas(this, size);
        _sources = [.. placements.Select((p, i) => new CompositeSource(this, i, p, p.X - left, p.Y - top, duplication?.Invoke(p) == true))];
        _compositor = new MonitorCompositor(_surface, [.. _sources.Select(s => new CanvasRect(s.CanvasX, s.CanvasY, s.Area.Width, s.Area.Height))], _qpcFrequency, fps);

        _stopEvent = PInvoke.CreateEvent(null, true, false, default(PCWSTR));
        _timer = PInvoke.CreateWaitableTimerEx(
            null,
            default(PCWSTR),
            PInvoke.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            (uint)(SYNCHRONIZATION_ACCESS_RIGHTS.TIMER_MODIFY_STATE | SYNCHRONIZATION_ACCESS_RIGHTS.SYNCHRONIZATION_SYNCHRONIZE));
        if (_stopEvent.IsNull || _timer.IsNull)
        {
            CloseHandles();
            _surface.Dispose();
            throw new InvalidOperationException("Could not create the compositor's slot clock (event or high-resolution timer).");
        }

        _clock = new Thread(RunClock) { Name = "Monitor compositor", IsBackground = true };
        logger.LogInformation("Compositing {Count} screen(s) into one {Size} canvas at {Fps} fps: {Monitors}",
            _sources.Length, size, fps, string.Join("; ", placements));
    }

    /// <summary>Every screen's source is gone; no more frames will arrive.</summary>
    public event Action? Closed;

    public event Action<Exception>? Failed;

    /// <summary>The canvas: the bounding box of the screens, in virtual-desktop pixels.</summary>
    public VideoSize CanvasSize { get; }

    /// <summary>Each screen's source, in placement order.</summary>
    public IReadOnlyList<CompositeSource> Sources => _sources;

    /// <summary>The compositor, for its counts.</summary>
    public MonitorCompositor Compositor => _compositor;

    /// <summary>The GDI device names of the screens still captured.</summary>
    public IReadOnlyList<string> LiveDevices => [.. _sources.Where(s => !s.Removed).Select(s => s.Placement.Monitor.DeviceName)];

    /// <summary>Whether any screen fell back to Desktop Duplication (or was asked to).</summary>
    public bool AnyDuplication => _sources.Any(s => s.Api == "desktop-duplication");

    /// <summary>
    /// Starts every screen's capture, then the slot clock. A screen whose capture can't start at all
    /// fails the start with its own error; the caller then disposes this, which stops the screens
    /// already started.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
        }

        foreach (var source in _sources)
        {
            source.Start();
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _clock.Start();
            _clockStarted = true;
        }
    }

    public void SetFps(int fps)
    {
        Volatile.Write(ref _fps, fps);
        _compositor.SetFps(fps);
        foreach (var source in _sources)
        {
            source.Capture?.SetFps(fps);
        }
    }

    /// <summary>
    /// Stops capturing the screen <paramref name="deviceName"/> (it was removed, T-03): its place
    /// turns black and the rest go on. True when it was captured; <see cref="Closed"/> follows once none is left.
    /// </summary>
    public bool Drop(string deviceName)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }
        }

        var source = _sources.FirstOrDefault(s => !s.Removed && string.Equals(s.Placement.Monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return false;
        }

        RemoveSource(source, "dropped");
        return true;
    }

    /// <summary>Stops the clock and every source, waiting for frames in flight, logs each source's arrivals and releases the canvas.</summary>
    public void Dispose()
    {
        bool clockStarted;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            clockStarted = _clockStarted;
            _stopping = true;
            PInvoke.SetEvent(_stopEvent);
        }

        // Only a clock that started is joined: a start that failed part-way never started it.
        if (clockStarted && Thread.CurrentThread != _clock)
        {
            _clock.Join();
        }

        foreach (var source in _sources)
        {
            source.Capture?.Dispose();
        }

        foreach (var source in _sources)
        {
            _logger.LogInformation("Composite source {Summary}", source.Summary());
        }

        _logger.LogInformation("Compositor: {Blits} frames drawn, {Presents} canvases handed on", _compositor.Blits, _compositor.Presents);
        CloseHandles();
        _surface.Dispose();
    }

    private void RemoveSource(CompositeSource source, string why)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        if (!source.MarkRemoved())
        {
            return;
        }

        _logger.LogWarning("Composite screen {Device} {Why}; its place is black", source.Placement.Monitor.DeviceName, why);
        source.Capture?.Dispose();
        if (_compositor.Remove(source.Index) == 0 && Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            Closed?.Invoke();
        }
    }

    private void RunClock()
    {
        try
        {
            while (!_stopping)
            {
                var now = Stopwatch.GetTimestamp();
                if (!WaitUntil(_compositor.NextTick(now)))
                {
                    return;
                }

                _compositor.Tick(Stopwatch.GetTimestamp());
            }
        }
        catch (Exception e)
        {
            _logger.LogError("The compositor's slot clock failed: {Message}", e.Message);
            Failed?.Invoke(e);
        }
    }

    private bool WaitUntil(long deadline)
    {
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0)
        {
            return !_stopping;
        }

        // A negative due time is relative, in 100 ns.
        var due = -Math.Max(1, (long)((Int128)remaining * HundredNanosecondsPerSecond / _qpcFrequency));
        var handles = stackalloc HANDLE[2] { _stopEvent, _timer };
        var woke = PInvoke.SetWaitableTimer(_timer, &due, 0, null, null, false)
            ? PInvoke.WaitForMultipleObjects(2, handles, false, Infinite)
            : PInvoke.WaitForMultipleObjects(1, handles, false, (uint)Math.Max(1, remaining * 1000 / _qpcFrequency));
        return woke != (WAIT_EVENT)WaitObject0 && !_stopping;
    }

    private void CloseHandles()
    {
        if (!_timer.IsNull)
        {
            PInvoke.CloseHandle(_timer);
            _timer = default;
        }

        if (!_stopEvent.IsNull)
        {
            PInvoke.CloseHandle(_stopEvent);
            _stopEvent = default;
        }
    }

    /// <summary>The canvas a <see cref="CompositeCapture"/> draws into: the GPU texture, or a test's fake.</summary>
    internal interface ICanvasTarget : ICanvasSurface, IDisposable;

    /// <summary>
    /// The canvas texture: a frame of the screen's size is copied into its place; one of another size
    /// (a mode change) is first scaled to the place's size, letterboxed, through that place's own scaler.
    /// </summary>
    private sealed class CanvasSurface : ICanvasTarget
    {
        private readonly CompositeCapture _owner;
        private readonly VideoSize _size;
        private readonly Dictionary<CanvasRect, (FrameScaler Scaler, nint Staging)> _scaled = [];
        private readonly D3D11Device _device;
        private readonly Lock _clearing = new();
        private nint _canvas;

        public CanvasSurface(CompositeCapture owner, VideoSize size)
        {
            _owner = owner;
            _device = owner._device!;
            _size = size;
            _canvas = _device.CreateTexture(size);
            if (_canvas == 0)
            {
                throw new InvalidOperationException($"Could not create the {size} canvas texture.");
            }

            _device.Clear(_canvas, 0, 0f, 0f, 0f);
        }

        public void Blit(nint texture, int contentWidth, int contentHeight, CanvasRect area)
        {
            var place = new VideoSize(area.Width, area.Height);
            var content = new VideoSize(contentWidth, contentHeight);
            var device = _device;
            if (content == place)
            {
                device.CopyFrameTo(texture, content, _canvas, area.X, area.Y, place);
                return;
            }

            if (!_scaled.TryGetValue(area, out var scaled))
            {
                var staging = _device.CreateTexture(place);
                scaled = (new FrameScaler(_device, place, nv12: false, targets: 1, _owner._logger), staging);
                _scaled[area] = scaled;
                _owner._logger.LogInformation("A screen at ({X},{Y}) delivers {Content} into its {Place} place: scaled from now on", area.X, area.Y, content, place);
            }

            scaled.Scaler.Draw(texture, content, scaled.Staging, 0);
            _device.CopyFrameTo(scaled.Staging, place, _canvas, area.X, area.Y, place);
        }

        public void Clear(CanvasRect area)
        {
            // A removal (the orchestrator's thread) may race a teardown: once the canvas is released, nothing to clear.
            lock (_clearing)
            {
                if (_canvas == 0)
                {
                    return;
                }

                // A black frame of the place's size, copied over it: Clear fills a whole texture.
                var black = _device.CreateTexture(new VideoSize(area.Width, area.Height));
                try
                {
                    _device.Clear(black, 0, 0f, 0f, 0f);
                    _device.CopyFrameTo(black, new VideoSize(area.Width, area.Height), _canvas, area.X, area.Y, new VideoSize(area.Width, area.Height));
                }
                finally
                {
                    D3D11Device.ReleaseTexture(black);
                }
            }
        }

        public void Present(long qpc) => _owner._onFrame(qpc, _canvas, _size);

        public void Dispose()
        {
            foreach (var (scaler, staging) in _scaled.Values)
            {
                scaler.Dispose();
                D3D11Device.ReleaseTexture(staging);
            }

            _scaled.Clear();
            lock (_clearing)
            {
                D3D11Device.ReleaseTexture(_canvas);
                _canvas = 0;
            }
        }
    }

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
                $"{Index} {Placement} via {Api}: {Frames} frames, {Fps:0.0} fps, interval p50 {IntervalPercentileMs(50):0.00} ms, p99 {IntervalPercentileMs(99):0.00} ms, max {MaxGapMs:0.0} ms, {LateFrames} late (> 2 slots){(Removed ? ", removed" : "")}");

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

            var device = _owner._device!;
            var handle = Placement.Monitor.Handle;
            if (!_duplication)
            {
                ICaptureSource? wgc = null;
                try
                {
                    wgc = WindowCapture.ForMonitor(device, handle, Volatile.Read(ref _owner._fps), OnFrame, _owner._logger);
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

            var duplication = new DesktopDuplicationCapture(device, handle, Volatile.Read(ref _owner._fps), OnFrame, _owner._logger);
            Wire(duplication);
            duplication.Start();
            Api = "desktop-duplication";
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
            capture.Failed += e => _owner.Failed?.Invoke(e);
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
