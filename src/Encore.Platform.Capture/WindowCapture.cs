using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Media;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.WinRT;
using Windows.Win32.System.WinRT.Graphics.Capture;

namespace Encore.Platform.Capture;

/// <summary>Windows.Graphics.Capture of one window or monitor, with no hooks; frames arrive one at a time on a pool thread.</summary>
public sealed unsafe class WindowCapture : CaptureSource
{
    private const string GraphicsCaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";

    // IGraphicsCaptureItem, the WinRT interface CreateForWindow returns, and IGraphicsCaptureItemInterop.
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    // The item's Closed event needs a message loop the engine doesn't have, so a watchdog polls IsWindow instead.
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(250);

    private readonly D3D11Device _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly Lock _gate = new();
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly HWND _window;
    private readonly Timer _watchdog;
    private SizeInt32 _poolSize;
    private void* _poolAbi;
    private bool _disposed;

    /// <summary>WGC's MinUpdateInterval is half a slot of fps, so a fast-repainting window can fill every slot with a new frame.</summary>
    public WindowCapture(D3D11Device device, nint window, int fps, CapturedFrameHandler onFrame, ILogger logger)
        : this(device, CreateItem(window, monitor: false), window, fps, onFrame, logger)
    {
    }

    private WindowCapture(D3D11Device device, GraphicsCaptureItem item, nint window, int fps, CapturedFrameHandler onFrame, ILogger logger)
        : base(onFrame, logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        _device = device;
        _window = new HWND((void*)window);

        _item = item;
        _item.Closed += OnItemClosed;
        _poolSize = _item.Size;

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _poolAbi = FrameAbi.QueryPool(_pool);
        _pool.FrameArrived += OnFrameArrived;

        _session = _pool.CreateCaptureSession(_item);
        _session.MinUpdateInterval = UpdateInterval(fps);
        TryDisableBorder();
        _watchdog = new Timer(_ => CheckWindow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        logger.LogInformation("Capturing {Kind} '{Title}' at {Width}x{Height}, at most {Fps} fps",
            window == 0 ? "monitor" : "window", _item.DisplayName, _poolSize.Width, _poolSize.Height, 2 * fps);
    }

    public VideoSize ItemSize => new(_item.Size.Width, _item.Size.Height);

    /// <summary>Captures a whole monitor; only the item's own Closed (the monitor detached) raises <see cref="CaptureSource.Closed"/>.</summary>
    public static WindowCapture ForMonitor(D3D11Device device, nint monitor, int fps, CapturedFrameHandler onFrame, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (monitor == 0)
        {
            throw new ArgumentException("No monitor was given.", nameof(monitor));
        }

        return new WindowCapture(device, CreateItem(monitor, monitor: true), 0, fps, onFrame, logger);
    }

    /// <summary>The size WGC captures <paramref name="window"/> at now.</summary>
    public static VideoSize SizeOf(nint window)
    {
        var size = CreateItem(window, monitor: false).Size;
        return new VideoSize(size.Width, size.Height);
    }

    /// <summary>Sets the output frame rate as the constructor does; a failure is logged and the old interval kept.</summary>
    public override void SetFps(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        try
        {
            _session.MinUpdateInterval = UpdateInterval(fps);
            LogRate(fps);
        }
        catch (Exception e)
        {
            Logger.LogWarning("The capture rate could not be set to {Fps} fps: {Message}", 2 * fps, e.Message);
        }
    }

    public override void Start()
    {
        _session.StartCapture();
        if (!_window.IsNull)
        {
            _watchdog.Change(WatchdogInterval, WatchdogInterval);
        }
    }

    /// <summary>Stops capture and waits for a frame in flight to finish.</summary>
    public override void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _watchdog.Dispose();
        _pool.FrameArrived -= OnFrameArrived;
        _item.Closed -= OnItemClosed;
        _session.Dispose();
        lock (_gate)
        {
            // No frame is in flight once the gate is ours, and none starts after _disposed.
            if (_poolAbi is not null)
            {
                ((IUnknown*)_poolAbi)->Release();
                _poolAbi = null;
            }
        }

        _pool.Dispose();
    }

    private static TimeSpan UpdateInterval(int fps) => TimeSpan.FromTicks(HalfSlot(TimeSpan.TicksPerSecond, fps));

    /// <summary>The capture item of a window, or of a monitor when <paramref name="monitor"/> is set.</summary>
    private static GraphicsCaptureItem CreateItem(nint handle, bool monitor)
    {
        void* factory = null;
        void* item = null;
        PInvoke.WindowsCreateString(GraphicsCaptureItemClass, (uint)GraphicsCaptureItemClass.Length, out var className).ThrowOnFailure();
        try
        {
            var interopIid = GraphicsCaptureItemInteropIid;
            PInvoke.RoGetActivationFactory(new HSTRING((void*)className.DangerousGetHandle()), &interopIid, &factory).ThrowOnFailure();

            var itemIid = GraphicsCaptureItemIid;
            var interop = (IGraphicsCaptureItemInterop*)factory;
            if (monitor)
            {
                interop->CreateForMonitor(new HMONITOR((void*)handle), &itemIid, &item);
            }
            else
            {
                interop->CreateForWindow(new HWND((void*)handle), &itemIid, &item);
            }

            return GraphicsCaptureItem.FromAbi((nint)item);
        }
        finally
        {
            if (item is not null) ((IUnknown*)item)->Release();
            if (factory is not null) ((IUnknown*)factory)->Release();
            className.Dispose();
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Per-frame calls use the raw ABI, because projected frames would allocate managed wrappers for every frame.
            var frame = FrameAbi.TryGetNextFrame(_poolAbi);
            if (frame is null)
            {
                return;
            }

            VideoSize content;
            try
            {
                content = DeliverFrame(frame);
            }
            finally
            {
                FrameAbi.Close(frame);
            }

            FollowResize(content);
        }
    }

    /// <summary>Hands one frame's texture to the handler and returns its content size.</summary>
    private VideoSize DeliverFrame(void* frame)
    {
        FrameAbi.Read(frame, out var content, out var relativeTime, out var surface);
        var qpc = QpcClock.From100ns(relativeTime, _qpcFrequency);
        var texture = FrameAbi.TextureOf(surface);
        try
        {
            Deliver(qpc, (nint)texture, content);
        }
        finally
        {
            texture->Release();
        }

        return content;
    }

    /// <summary>Recreates the frame pool at a window's new size, so later frames aren't cropped.</summary>
    private void FollowResize(VideoSize content)
    {
        if (content.Width == _poolSize.Width && content.Height == _poolSize.Height)
        {
            return;
        }

        Logger.LogInformation("Window resized to {Width}x{Height}; recreating the frame pool", content.Width, content.Height);
        _poolSize = new SizeInt32(content.Width, content.Height);
        _pool.Recreate(_device.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => ReportClosed("item Closed");

    private void CheckWindow()
    {
        if (!PInvoke.IsWindow(_window))
        {
            ReportClosed("window destroyed");
        }
    }

    private void ReportClosed(string how)
    {
        if (TryClaimClose())
        {
            Logger.LogInformation("Captured window closed ({How})", how);
            RaiseClosed();
        }
    }

    private void TryDisableBorder()
    {
        try
        {
            var access = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().GetAwaiter().GetResult();
            _session.IsBorderRequired = false;
            Logger.LogInformation("Capture border disabled (borderless access {Access})", access);
        }
        catch (Exception e)
        {
            Logger.LogWarning("Capture border could not be disabled: {Message}", e.Message);
        }
    }
}
