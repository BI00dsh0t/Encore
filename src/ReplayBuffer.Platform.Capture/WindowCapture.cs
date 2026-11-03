using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Media;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
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

    // The per-frame ABI: IDirect3D11CaptureFramePool, IDirect3D11CaptureFrame and IClosable, and
    // their vtable slots (after IInspectable's six).
    private static readonly Guid FramePoolIid = new("24EB6D22-1975-422E-82E7-780DBD8DDF24");
    private static readonly Guid ClosableIid = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private const int TryGetNextFrameSlot = 7; // IDirect3D11CaptureFramePool: Recreate, TryGetNextFrame, ...
    private const int SurfaceSlot = 6;         // IDirect3D11CaptureFrame: get_Surface, get_SystemRelativeTime, get_ContentSize
    private const int SystemRelativeTimeSlot = 7;
    private const int ContentSizeSlot = 8;
    private const int CloseSlot = 6;           // IClosable: Close

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
    private void* _poolAbi;
    private bool _disposed;

    /// <param name="fps">
    /// The output frame rate. WGC's <c>MinUpdateInterval</c> is half a slot, so a window repainting
    /// faster than <paramref name="fps"/> can fill every slot with a new frame.
    /// </param>
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
        _poolAbi = PoolAbi(_pool);
        _pool.FrameArrived += OnFrameArrived;

        _session = _pool.CreateCaptureSession(_item);
        _session.MinUpdateInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / (2L * fps));
        TryDisableBorder();
        _watchdog = new Timer(_ => CheckWindow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        logger.LogInformation("Capturing window '{Title}' at {Width}x{Height}, at most {Fps} fps", _item.DisplayName, _poolSize.Width, _poolSize.Height, 2 * fps);
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

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // The per-frame calls go through the ABI (AD-14 hand-offs): projected frames and
            // surfaces would allocate managed wrappers for every frame (AD-7).
            void* frame = null;
            var pool = _poolAbi;
            if (((delegate* unmanaged[Stdcall]<void*, void**, HRESULT>)(*(void***)pool)[TryGetNextFrameSlot])(pool, &frame).Failed || frame is null)
            {
                return;
            }

            AbiSize content;
            try
            {
                long relativeTime;
                void* surface = null;
                var vtable = *(void***)frame;
                ((delegate* unmanaged[Stdcall]<void*, AbiSize*, HRESULT>)vtable[ContentSizeSlot])(frame, &content).ThrowOnFailure();
                ((delegate* unmanaged[Stdcall]<void*, long*, HRESULT>)vtable[SystemRelativeTimeSlot])(frame, &relativeTime).ThrowOnFailure();
                ((delegate* unmanaged[Stdcall]<void*, void**, HRESULT>)vtable[SurfaceSlot])(frame, &surface).ThrowOnFailure();

                var qpc = QpcClock.From100ns(relativeTime, _qpcFrequency);
                var texture = GetTexture(surface);
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
            }
            finally
            {
                Close(frame);
            }

            if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
            {
                _logger.LogInformation("Window resized to {Width}x{Height}; recreating the frame pool", content.Width, content.Height);
                _poolSize = new SizeInt32(content.Width, content.Height);
                _pool.Recreate(_device.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            }
        }
    }

    /// <summary>The pool's <c>IDirect3D11CaptureFramePool</c> ABI pointer, owned by the caller.</summary>
    private static void* PoolAbi(Direct3D11CaptureFramePool pool)
    {
        var unknown = (void*)MarshalInspectable<object>.FromManaged(pool);
        try
        {
            void* abi = null;
            var iid = FramePoolIid;
            ((IUnknown*)unknown)->QueryInterface(&iid, &abi).ThrowOnFailure();
            return abi;
        }
        finally
        {
            ((IUnknown*)unknown)->Release();
        }
    }

    /// <summary>Closes a frame (handing its buffer back to the pool) and releases it.</summary>
    private static void Close(void* frame)
    {
        void* closable = null;
        var closableIid = ClosableIid;
        if (((IUnknown*)frame)->QueryInterface(&closableIid, &closable).Succeeded)
        {
            _ = ((delegate* unmanaged[Stdcall]<void*, HRESULT>)(*(void***)closable)[CloseSlot])(closable);
            ((IUnknown*)closable)->Release();
        }

        ((IUnknown*)frame)->Release();
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

    /// <summary>The surface's <c>ID3D11Texture2D</c>; releases <paramref name="surface"/>, the caller releases the texture.</summary>
    private static ID3D11Texture2D* GetTexture(void* surface)
    {
        void* access = null;
        try
        {
            var accessIid = DxgiInterfaceAccessIid;
            ((IUnknown*)surface)->QueryInterface(&accessIid, &access).ThrowOnFailure();

            void* texture = null;
            var textureIid = ID3D11Texture2D.IID_Guid;
            ((IDirect3DDxgiInterfaceAccess*)access)->GetInterface(&textureIid, &texture);
            return (ID3D11Texture2D*)texture;
        }
        finally
        {
            if (access is not null) ((IUnknown*)access)->Release();
            ((IUnknown*)surface)->Release();
        }
    }

    /// <summary>The ABI <c>SizeInt32</c>.</summary>
    private struct AbiSize
    {
        public int Width;
        public int Height;
    }
}
