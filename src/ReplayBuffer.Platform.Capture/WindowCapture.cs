using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Media;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.System.Com;
using Windows.Win32.System.WinRT;
using Windows.Win32.System.WinRT.Direct3D11;
using Windows.Win32.System.WinRT.Graphics.Capture;
using WinRT;

namespace ReplayBuffer.Platform.Capture;

/// <summary>
/// Receives one captured frame. <paramref name="texture"/> is an <c>ID3D11Texture2D*</c>
/// valid only for the duration of the call; <paramref name="qpc"/> is its capture QPC.
/// </summary>
public delegate void CapturedFrameHandler(long qpc, nint texture, VideoSize contentSize);

/// <summary>
/// Windows.Graphics.Capture of one window (AD-12): a window item only, no hooks, no
/// process access. Frames arrive on a free-threaded pool's worker thread, one at a time.
/// </summary>
public sealed unsafe class WindowCapture : IDisposable
{
    private const string GraphicsCaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";

    // IGraphicsCaptureItem, the WinRT interface CreateForWindow returns.
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    // IGraphicsCaptureItemInterop and IDirect3DDxgiInterfaceAccess.
    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    // The item's Closed event needs a message loop on the creating thread, which the headless
    // engine doesn't have; a watchdog on IsWindow raises Closed when the window is destroyed.
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(250);

    private readonly D3D11Device _device;
    private readonly CapturedFrameHandler _onFrame;
    private readonly ILogger _logger;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly Lock _gate = new();
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly HWND _hwnd;
    private readonly Timer _watchdog;
    private int _closedRaised;
    private SizeInt32 _poolSize;
    private bool _disposed;

    /// <param name="fps">The most frames a second WGC delivers: its <c>MinUpdateInterval</c> is 1/<paramref name="fps"/>.</param>
    public WindowCapture(D3D11Device device, nint hwnd, int fps, CapturedFrameHandler onFrame, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        _device = device;
        _onFrame = onFrame;
        _logger = logger;
        _hwnd = new HWND((void*)hwnd);

        _item = CreateItem(hwnd);
        _item.Closed += OnItemClosed;
        _poolSize = _item.Size;

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;

        _session = _pool.CreateCaptureSession(_item);
        _session.MinUpdateInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / fps);
        TryDisableBorder();
        _watchdog = new Timer(_ => CheckWindow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        logger.LogInformation("Capturing window '{Title}' at {Width}x{Height}, at most {Fps} fps", _item.DisplayName, _poolSize.Width, _poolSize.Height, fps);
    }

    /// <summary>The size WGC captures <paramref name="hwnd"/> at now.</summary>
    public static VideoSize SizeOf(nint hwnd)
    {
        var size = CreateItem(hwnd).Size;
        return new VideoSize(size.Width, size.Height);
    }

    /// <summary>The window was closed; no more frames will arrive.</summary>
    public event Action? Closed;

    /// <summary>The frame handler threw; capture continues, the caller decides.</summary>
    public event Action<Exception>? Failed;

    public VideoSize ItemSize => new(_item.Size.Width, _item.Size.Height);

    public void Start()
    {
        _session.StartCapture();
        _watchdog.Change(WatchdogInterval, WatchdogInterval);
    }

    /// <summary>Stops capture and waits for a frame in flight to finish.</summary>
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

        _watchdog.Dispose();
        _pool.FrameArrived -= OnFrameArrived;
        _item.Closed -= OnItemClosed;
        _session.Dispose();
        _pool.Dispose();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var content = frame.ContentSize;
            var qpc = QpcClock.From100ns(frame.SystemRelativeTime.Ticks, _qpcFrequency);
            var texture = GetTexture(frame.Surface);
            try
            {
                _onFrame(qpc, (nint)texture, new VideoSize(content.Width, content.Height));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Frame handler failed");
                Failed?.Invoke(e);
            }
            finally
            {
                texture->Release();
            }

            if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
            {
                _logger.LogInformation("Window resized to {Width}x{Height}; recreating the frame pool", content.Width, content.Height);
                _poolSize = content;
                sender.Recreate(_device.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
            }
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => RaiseClosed("item Closed");

    private void CheckWindow()
    {
        if (!PInvoke.IsWindow(_hwnd))
        {
            RaiseClosed("window destroyed");
        }
    }

    private void RaiseClosed(string how)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            _logger.LogInformation("Captured window closed ({How})", how);
            Closed?.Invoke();
        }
    }

    private void TryDisableBorder()
    {
        try
        {
            var access = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().GetAwaiter().GetResult();
            _session.IsBorderRequired = false;
            _logger.LogInformation("Capture border disabled (borderless access {Access})", access);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Capture border could not be disabled: {Message}", e.Message);
        }
    }

    private static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        void* factory = null;
        void* item = null;
        PInvoke.WindowsCreateString(GraphicsCaptureItemClass, (uint)GraphicsCaptureItemClass.Length, out var className).ThrowOnFailure();
        try
        {
            var interopIid = GraphicsCaptureItemInteropIid;
            PInvoke.RoGetActivationFactory(new HSTRING((void*)className.DangerousGetHandle()), &interopIid, &factory).ThrowOnFailure();

            var itemIid = GraphicsCaptureItemIid;
            ((IGraphicsCaptureItemInterop*)factory)->CreateForWindow(new HWND((void*)hwnd), &itemIid, &item);
            return GraphicsCaptureItem.FromAbi((nint)item);
        }
        finally
        {
            if (item is not null) ((IUnknown*)item)->Release();
            if (factory is not null) ((IUnknown*)factory)->Release();
            className.Dispose();
        }
    }

    private static ID3D11Texture2D* GetTexture(IDirect3DSurface surface)
    {
        var surfaceAbi = (void*)MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        void* access = null;
        try
        {
            var accessIid = DxgiInterfaceAccessIid;
            ((IUnknown*)surfaceAbi)->QueryInterface(&accessIid, &access).ThrowOnFailure();

            void* texture = null;
            var textureIid = ID3D11Texture2D.IID_Guid;
            ((IDirect3DDxgiInterfaceAccess*)access)->GetInterface(&textureIid, &texture);
            return (ID3D11Texture2D*)texture;
        }
        finally
        {
            if (access is not null) ((IUnknown*)access)->Release();
            ((IUnknown*)surfaceAbi)->Release();
        }
    }
}
