using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Media;

namespace Encore.Platform.Capture;

/// <summary>A multi-monitor recording's source: every screen is drawn into one canvas. Details: docs/code-notes.md.</summary>
public sealed partial class CompositeCapture : CaptureSource
{
    private readonly D3D11Device? _device;
    private readonly Func<CompositeSource, CapturedFrameHandler, ICaptureSource>? _sourceFactory;
    private readonly Lock _gate = new();
    private readonly CompositeSource[] _sources;
    private readonly MonitorCompositor _compositor;
    private readonly ICanvasTarget _surface;
    private readonly Thread _clock;
    private readonly StoppableTimer _timer;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private volatile bool _stopping;
    private int _fps;
    private bool _started;
    private bool _clockStarted;
    private bool _disposed;

    /// <summary>The duplication argument says which screens use Desktop Duplication instead of WGC; null means none.</summary>
    public CompositeCapture(
        D3D11Device device,
        IReadOnlyList<MonitorPlacement> placements,
        int fps,
        Func<MonitorPlacement, bool>? duplication,
        CapturedFrameHandler onFrame,
        ILogger logger)
        : this(
            device ?? throw new ArgumentNullException(nameof(device)),
            placements,
            fps,
            duplication,
            null,
            (owner, size) => new CanvasSurface(owner, size),
            onFrame,
            logger)
    {
    }

    /// <summary>For tests: the screens' captures and the canvas are passed in, so no GPU device is needed.</summary>
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
        : base(onFrame, logger)
    {
        ArgumentOutOfRangeException.ThrowIfZero(placements.Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        _device = device;
        _sourceFactory = sources;
        _fps = fps;

        var (size, left, top) = MonitorCanvas.Bounds(placements);
        CanvasSize = size;
        _surface = canvas(this, size);
        _sources = [.. placements.Select((placement, index) => new CompositeSource(
            this, index, placement, placement.X - left, placement.Y - top, duplication?.Invoke(placement) == true))];
        _compositor = new MonitorCompositor(
            _surface,
            [.. _sources.Select(source => new CanvasRect(source.CanvasX, source.CanvasY, source.Area.Width, source.Area.Height))],
            _qpcFrequency,
            fps);

        var timer = StoppableTimer.TryCreate();
        if (timer is null)
        {
            _surface.Dispose();
            throw new InvalidOperationException("Could not create the compositor's slot clock (event or high-resolution timer).");
        }

        _timer = timer;
        _clock = new Thread(RunClock) { Name = "Monitor compositor", IsBackground = true };
        logger.LogInformation("Compositing {Count} screen(s) into one {Size} canvas at {Fps} fps: {Monitors}",
            _sources.Length, size, fps, string.Join("; ", placements));
    }

    /// <summary>The canvas: the bounding box of the screens, in virtual-desktop pixels.</summary>
    public VideoSize CanvasSize { get; }

    /// <summary>Each screen's source, in placement order.</summary>
    public IReadOnlyList<CompositeSource> Sources => _sources;

    /// <summary>The GDI device names of the screens still captured.</summary>
    public IReadOnlyList<string> LiveDevices =>
        [.. _sources.Where(source => !source.Removed).Select(source => source.Placement.Monitor.DeviceName)];

    /// <summary>Starts every screen's capture, then the slot clock; when one fails, the caller disposes this to stop the rest.</summary>
    public override void Start()
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

    public override void SetFps(int fps)
    {
        Volatile.Write(ref _fps, fps);
        _compositor.SetFps(fps);
        foreach (var source in _sources)
        {
            source.Capture?.SetFps(fps);
        }
    }

    /// <summary>Stops capturing a removed screen: its place turns black and the rest go on. True when it was captured.</summary>
    public bool Drop(string deviceName)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }
        }

        var source = _sources.FirstOrDefault(source => !source.Removed && source.Shows(deviceName));
        if (source is null)
        {
            return false;
        }

        RemoveSource(source, "dropped");
        return true;
    }

    /// <summary>Stops the clock and every source, waits for frames in flight, logs arrivals and releases the canvas.</summary>
    public override void Dispose()
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
            _timer.Stop();
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
            Logger.LogInformation("Composite source {Summary}", source.Summary());
        }

        Logger.LogInformation("Compositor: {Blits} frames drawn, {Presents} canvases handed on", _compositor.Blits, _compositor.Presents);
        _timer.Dispose();
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

        Logger.LogWarning("Composite screen {Device} {Why}; its place is black", source.Placement.Monitor.DeviceName, why);
        source.Capture?.Dispose();
        if (_compositor.Remove(source.Index) == 0 && TryClaimClose())
        {
            RaiseClosed();
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
            Logger.LogError("The compositor's slot clock failed: {Message}", e.Message);
            RaiseFailed(e);
        }
    }

    private bool WaitUntil(long deadline) => _timer.WaitUntil(deadline) && !_stopping;
}
