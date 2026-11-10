using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.Threading;

namespace Encore.Platform.Capture;

/// <summary>Desktop Duplication of one monitor: the fallback when WGC can't capture it, never for games. docs/code-notes.md.</summary>
public sealed unsafe class DesktopDuplicationCapture : ICaptureSource
{
    private const uint RetryMs = 100;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;
    private const long HundredNanosecondsPerSecond = 10_000_000;

    private readonly IOutputDuplication _duplication;
    private readonly CapturedFrameHandler _onFrame;
    private readonly ILogger _logger;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly Lock _gate = new();
    private readonly Thread _thread;
    private HANDLE _stopEvent;
    private HANDLE _timer;
    private long _intervalQpc;
    private long _createdQpc;
    private int _simulateLost;
    private int _closedRaised;
    private volatile bool _stopping;
    private bool _started;
    private bool _exited;
    private bool _disposed;

    /// <summary>Frames are delivered at most 2 × fps a second; throws when the monitor isn't an output of the device's adapter.</summary>
    public DesktopDuplicationCapture(D3D11Device device, nint hmonitor, int fps, CapturedFrameHandler onFrame, ILogger logger)
        : this(new DxgiOutputDuplication(device ?? throw new ArgumentNullException(nameof(device)), ValidMonitor(hmonitor)), fps, onFrame, logger)
    {
    }

    /// <summary>For tests: captures through <paramref name="duplication"/>, a fake of DXGI's.</summary>
    internal DesktopDuplicationCapture(IOutputDuplication duplication, int fps, CapturedFrameHandler onFrame, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        _duplication = duplication;
        _onFrame = onFrame;
        _logger = logger;
        _intervalQpc = HalfSlot(fps);

        var hr = duplication.Duplicate();
        if (hr == HRESULT.DXGI_ERROR_NOT_FOUND)
        {
            throw new InvalidOperationException("The monitor is not an output of the capture device's adapter.");
        }

        hr.ThrowOnFailure();
        _createdQpc = Stopwatch.GetTimestamp();

        _stopEvent = PInvoke.CreateEvent(null, true, false, default(PCWSTR));
        _timer = PInvoke.CreateWaitableTimerEx(
            null,
            default(PCWSTR),
            PInvoke.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            (uint)(SYNCHRONIZATION_ACCESS_RIGHTS.TIMER_MODIFY_STATE | SYNCHRONIZATION_ACCESS_RIGHTS.SYNCHRONIZATION_SYNCHRONIZE));
        if (_stopEvent.IsNull || _timer.IsNull)
        {
            duplication.Release();
            CloseHandles();
            throw new InvalidOperationException("Could not create the Desktop Duplication thread's event or high-resolution timer.");
        }

        var size = duplication.ModeSize;
        _thread = new Thread(Run) { Name = "Desktop Duplication", IsBackground = true };
        logger.LogInformation("Capturing monitor through Desktop Duplication at {Width}x{Height}, at most {Fps} fps",
            size.Width, size.Height, 2 * fps);
    }

    /// <summary>The monitor's output left the adapter; no more frames will arrive.</summary>
    public event Action? Closed;

    /// <summary>The frame handler threw (capture continues), or the duplication itself failed (its thread ends).</summary>
    public event Action<Exception>? Failed;

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

        _thread.Start();
    }

    /// <summary>Delivers at most <c>2 × <paramref name="fps"/></c> frames a second from the next frame on.</summary>
    public void SetFps(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        Interlocked.Exchange(ref _intervalQpc, HalfSlot(fps));
        _logger.LogInformation("Capturing at most {Fps} fps", 2 * fps);
    }

    /// <summary>For tests: the next acquire behaves as if it returned <c>DXGI_ERROR_ACCESS_LOST</c>.</summary>
    internal void SimulateAccessLost() => Interlocked.Exchange(ref _simulateLost, 1);

    /// <summary>Stops the thread, waiting for a frame in flight to finish, and releases the duplication.</summary>
    public void Dispose()
    {
        bool started;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            started = _started;
            _stopping = true;
            if (!_exited)
            {
                // Under the gate: the thread doesn't close the event while it may still be set here.
                PInvoke.SetEvent(_stopEvent);
            }
        }

        if (!started)
        {
            _duplication.Release();
            CloseHandles();
        }
        else if (Thread.CurrentThread != _thread)
        {
            // The thread releases the duplication as it exits.
            _thread.Join();
            CloseHandles();
        }

        // Called from the thread itself (a frame handler ending the session), it closes the handles as it exits.
    }

    private void Run()
    {
        try
        {
            long nextPoll = 0;
            long lastQpc = 0;

            // The first frame of a duplication is the whole desktop, delivered even when static.
            var fresh = true;
            while (!_stopping)
            {
                if (Interlocked.Exchange(ref _simulateLost, 0) == 1)
                {
                    _logger.LogInformation("Desktop Duplication access lost (simulated)");
                    if (!Recreate())
                    {
                        return;
                    }

                    fresh = true;
                    continue;
                }

                if (!WaitUntil(nextPoll))
                {
                    return;
                }

                // Whatever the poll finds, the next one is half a slot on: never a busy loop.
                var hr = _duplication.TryAcquire(out var frame);
                nextPoll = Stopwatch.GetTimestamp() + Interlocked.Read(ref _intervalQpc);
                if (hr == HRESULT.DXGI_ERROR_WAIT_TIMEOUT)
                {
                    continue;
                }

                if (hr == HRESULT.DXGI_ERROR_ACCESS_LOST)
                {
                    _logger.LogInformation("Desktop Duplication access lost; recreating it");
                    if (!Recreate())
                    {
                        return;
                    }

                    fresh = true;
                    continue;
                }

                // Any other failure (a removed device, say) can't be recreated: it fails the source.
                hr.ThrowOnFailure();

                // A pointer-only or stale update carries no new image; a first image is stamped no earlier than the creation.
                var qpc = fresh ? Math.Max(frame.LastPresentTime, _createdQpc) : frame.LastPresentTime;
                if (frame.Texture != 0 && (fresh || frame.LastPresentTime != 0) && qpc > lastQpc)
                {
                    Deliver(qpc, frame);
                    lastQpc = qpc;
                }

                fresh = false;
                hr = _duplication.ReleaseFrame();
                if (hr == HRESULT.DXGI_ERROR_ACCESS_LOST && !_stopping)
                {
                    _logger.LogInformation("Desktop Duplication access lost at a frame release; recreating it");
                    if (!Recreate())
                    {
                        return;
                    }

                    fresh = true;
                }
                else if (!_stopping)
                {
                    hr.ThrowOnFailure();
                }
            }
        }
        catch (Exception e)
        {
            // Never take the process down from a capture thread: Failed tells the session, which fails.
            _logger.LogError(e, "Desktop Duplication thread failed");
            Failed?.Invoke(e);
        }
        finally
        {
            _duplication.Release();
            lock (_gate)
            {
                _exited = true;
                if (_disposed)
                {
                    CloseHandles();
                }
            }
        }
    }

    private void Deliver(long qpc, DuplicatedFrame frame)
    {
        try
        {
            _onFrame(qpc, frame.Texture, frame.Size);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Frame handler failed");
            Failed?.Invoke(e);
        }
    }

    /// <summary>Recreates the duplication, retrying every 100 ms; false when stopping or when the monitor's output is gone.</summary>
    private bool Recreate()
    {
        _duplication.Release();
        var logged = false;
        while (!_stopping)
        {
            var hr = _duplication.Duplicate();
            if (hr.Succeeded)
            {
                _createdQpc = Stopwatch.GetTimestamp();
                _logger.LogInformation("Desktop Duplication recreated");
                return true;
            }

            if (hr == HRESULT.DXGI_ERROR_NOT_FOUND)
            {
                if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
                {
                    _logger.LogInformation("Captured monitor closed (its output is gone)");
                    Closed?.Invoke();
                }

                return false;
            }

            // A removed or reset device never duplicates again: it fails the source.
            if (hr == DxgiOutputDuplication.DeviceRemoved || hr == DxgiOutputDuplication.DeviceReset)
            {
                hr.ThrowOnFailure();
            }

            if (!logged)
            {
                _logger.LogInformation("Desktop Duplication could not be recreated yet (0x{HResult:X8}); retrying every {Ms} ms", hr.Value, RetryMs);
                logged = true;
            }

            var stop = _stopEvent;
            if (PInvoke.WaitForMultipleObjects(1, &stop, false, RetryMs) == (WAIT_EVENT)WaitObject0)
            {
                return false;
            }
        }

        return false;
    }

    private static nint ValidMonitor(nint hmonitor) =>
        hmonitor == 0 ? throw new ArgumentException("No monitor was given.", nameof(hmonitor)) : hmonitor;

    /// <summary>Sleeps until <paramref name="deadline"/> (QPC); false when stopping.</summary>
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

    private long HalfSlot(int fps) => _qpcFrequency / (2L * fps);

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
}

/// <summary>A frame acquired from a duplication, valid until released; a pointer-only update has time 0 and no texture.</summary>
internal readonly record struct DuplicatedFrame(long LastPresentTime, nint Texture, VideoSize Size);

/// <summary>One monitor's output duplication, faked in tests; TryAcquire never waits, as DXGI would hold the device lock.</summary>
internal interface IOutputDuplication
{
    VideoSize ModeSize { get; }

    /// <summary>Creates the duplication; <c>DXGI_ERROR_NOT_FOUND</c> when the monitor isn't an output of the adapter.</summary>
    HRESULT Duplicate();

    /// <summary>Releases the duplication, if any.</summary>
    void Release();

    /// <summary>Acquires the next frame without waiting; <c>DXGI_ERROR_WAIT_TIMEOUT</c> means the desktop hasn't changed.</summary>
    HRESULT TryAcquire(out DuplicatedFrame frame);

    /// <summary>Releases the frame <see cref="TryAcquire"/> acquired.</summary>
    HRESULT ReleaseFrame();
}

/// <summary>DXGI's <c>IDXGIOutputDuplication</c> of the device adapter's output that shows a monitor.</summary>
internal sealed unsafe class DxgiOutputDuplication(D3D11Device device, nint hmonitor) : IOutputDuplication
{
    public static readonly HRESULT DeviceRemoved = new(unchecked((int)0x887A0005)); // DXGI_ERROR_DEVICE_REMOVED
    public static readonly HRESULT DeviceReset = new(unchecked((int)0x887A0007));   // DXGI_ERROR_DEVICE_RESET

    // The raw vtable slots, for their HRESULTs: CsWin32's struct methods throw on failure.
    private const int GetAdapterSlot = 7;        // IDXGIDevice: IDXGIObject's four, then GetAdapter
    private const int DuplicateOutputSlot = 22;  // IDXGIOutput1
    private const int AcquireNextFrameSlot = 8;  // IDXGIOutputDuplication
    private const int ReleaseFrameSlot = 14;

    private readonly HMONITOR _monitor = new((void*)hmonitor);
    private IDXGIOutputDuplication* _duplication;
    private IDXGIResource* _resource;
    private ID3D11Texture2D* _texture;

    public VideoSize ModeSize
    {
        get
        {
            _duplication->GetDesc(out var desc);
            return new VideoSize((int)desc.ModeDesc.Width, (int)desc.ModeDesc.Height);
        }
    }

    public HRESULT TryAcquire(out DuplicatedFrame frame)
    {
        frame = default;
        DXGI_OUTDUPL_FRAME_INFO info;
        IDXGIResource* resource = null;
        var duplication = _duplication;

        // A zero timeout: while AcquireNextFrame waits it holds the device lock the encoder needs.
        var hr = ((delegate* unmanaged[Stdcall]<IDXGIOutputDuplication*, uint, DXGI_OUTDUPL_FRAME_INFO*, IDXGIResource**, HRESULT>)
            (*(void***)duplication)[AcquireNextFrameSlot])(duplication, 0, &info, &resource);
        if (hr.Failed)
        {
            return hr;
        }

        _resource = resource;
        if (resource is null)
        {
            frame = new DuplicatedFrame(info.LastPresentTime, 0, default);
            return hr;
        }

        ID3D11Texture2D* texture = null;
        var textureIid = ID3D11Texture2D.IID_Guid;
        ((IUnknown*)resource)->QueryInterface(&textureIid, (void**)&texture).ThrowOnFailure();
        _texture = texture;
        texture->GetDesc(out var desc);
        frame = new DuplicatedFrame(info.LastPresentTime, (nint)texture, new VideoSize((int)desc.Width, (int)desc.Height));
        return hr;
    }

    public HRESULT ReleaseFrame()
    {
        ReleaseResource();
        var duplication = _duplication;
        return ((delegate* unmanaged[Stdcall]<IDXGIOutputDuplication*, HRESULT>)(*(void***)duplication)[ReleaseFrameSlot])(duplication);
    }

    public void Release()
    {
        ReleaseResource();
        if (_duplication is not null)
        {
            _duplication->Release();
            _duplication = null;
        }
    }

    /// <summary>Duplicates the device adapter's output that shows the monitor.</summary>
    public HRESULT Duplicate()
    {
        IDXGIDevice* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        IDXGIOutput1* output = null;
        try
        {
            var dxgiIid = IDXGIDevice.IID_Guid;
            var hr = ((IUnknown*)device.Device)->QueryInterface(&dxgiIid, (void**)&dxgiDevice);
            if (hr.Failed)
            {
                return hr;
            }

            hr = ((delegate* unmanaged[Stdcall]<IDXGIDevice*, IDXGIAdapter**, HRESULT>)(*(void***)dxgiDevice)[GetAdapterSlot])(dxgiDevice, &adapter);
            if (hr.Failed)
            {
                return hr;
            }

            for (uint i = 0; ; i++)
            {
                IDXGIOutput* candidate = null;
                hr = adapter->EnumOutputs(i, &candidate);
                if (hr.Failed)
                {
                    return hr;
                }

                if (candidate->GetDesc().Monitor == _monitor)
                {
                    var output1Iid = IDXGIOutput1.IID_Guid;
                    hr = ((IUnknown*)candidate)->QueryInterface(&output1Iid, (void**)&output);
                    candidate->Release();
                    if (hr.Failed)
                    {
                        return hr;
                    }

                    break;
                }

                candidate->Release();
            }

            IDXGIOutputDuplication* created = null;
            hr = ((delegate* unmanaged[Stdcall]<IDXGIOutput1*, IUnknown*, IDXGIOutputDuplication**, HRESULT>)(*(void***)output)[DuplicateOutputSlot])(
                output, (IUnknown*)device.Device, &created);
            if (hr.Succeeded)
            {
                _duplication = created;
            }

            return hr;
        }
        finally
        {
            if (output is not null) output->Release();
            if (adapter is not null) adapter->Release();
            if (dxgiDevice is not null) dxgiDevice->Release();
        }
    }

    private void ReleaseResource()
    {
        if (_texture is not null)
        {
            _texture->Release();
            _texture = null;
        }

        if (_resource is not null)
        {
            _resource->Release();
            _resource = null;
        }
    }
}
