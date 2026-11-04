using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.Threading;

namespace ReplayBuffer.Platform.Capture;

/// <summary>
/// DXGI Desktop Duplication of one monitor (AD-12's monitor-only fallback, 6.10), for a Monitor
/// Recording whose WGC item can't be created or started; never for game capture. Frames come only
/// when the desktop changes, stamped with DXGI's <c>LastPresentTime</c> (a QPC, AD-6), on a thread
/// of its own, one at a time, at most twice a slot of the fps: the thread waits half a slot after
/// each delivered frame before it acquires the next, so DXGI accumulates the updates meanwhile and
/// the latest image is never lost. <c>DXGI_ERROR_ACCESS_LOST</c> (a desktop switch, a mode change)
/// recreates the duplication, retrying every 100 ms; the monitor's output leaving the adapter
/// raises <see cref="Closed"/>, and any other failure (a removed device) raises <see cref="Failed"/>
/// and ends the thread. Windows marked <c>WDA_EXCLUDEFROMCAPTURE</c> don't appear in it. The
/// pointer is not drawn, and a rotated output's frames are its unrotated surface.
/// </summary>
public sealed unsafe class DesktopDuplicationCapture : ICaptureSource
{
    private const uint AcquireTimeoutMs = 100;
    private const uint RetryMs = 100;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;
    private const long HundredNanosecondsPerSecond = 10_000_000;
    private static readonly HRESULT DeviceRemoved = new(unchecked((int)0x887A0005)); // DXGI_ERROR_DEVICE_REMOVED
    private static readonly HRESULT DeviceReset = new(unchecked((int)0x887A0007));   // DXGI_ERROR_DEVICE_RESET

    // The raw vtable slots, for their HRESULTs: CsWin32's struct methods throw on failure.
    private const int GetAdapterSlot = 7;        // IDXGIDevice: IDXGIObject's four, then GetAdapter
    private const int DuplicateOutputSlot = 22;  // IDXGIOutput1
    private const int AcquireNextFrameSlot = 8;  // IDXGIOutputDuplication
    private const int ReleaseFrameSlot = 14;

    private readonly D3D11Device _device;
    private readonly HMONITOR _monitor;
    private readonly CapturedFrameHandler _onFrame;
    private readonly ILogger _logger;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly Lock _gate = new();
    private readonly Thread _thread;
    private HANDLE _stopEvent;
    private HANDLE _timer;
    private IDXGIOutputDuplication* _duplication;
    private long _intervalQpc;
    private long _createdQpc;
    private int _simulateLost;
    private int _closedRaised;
    private volatile bool _stopping;
    private bool _started;
    private bool _exited;
    private bool _disposed;

    /// <param name="fps">The output frame rate; frames are delivered at most <c>2 × fps</c> a second.</param>
    /// <exception cref="InvalidOperationException">The monitor isn't an output of the device's adapter.</exception>
    /// <exception cref="Exception">The duplication couldn't be created (its HRESULT).</exception>
    public DesktopDuplicationCapture(D3D11Device device, nint hmonitor, int fps, CapturedFrameHandler onFrame, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        if (hmonitor == 0)
        {
            throw new ArgumentException("No monitor was given.", nameof(hmonitor));
        }

        _device = device;
        _monitor = new HMONITOR((void*)hmonitor);
        _onFrame = onFrame;
        _logger = logger;
        _intervalQpc = HalfSlot(fps);

        var hr = TryDuplicate(out _duplication);
        if (hr == HRESULT.DXGI_ERROR_NOT_FOUND)
        {
            throw new InvalidOperationException("The monitor is not an output of the capture device's adapter.");
        }

        hr.ThrowOnFailure();

        _stopEvent = PInvoke.CreateEvent(null, true, false, default(PCWSTR));
        _timer = PInvoke.CreateWaitableTimerEx(
            null,
            default(PCWSTR),
            PInvoke.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            (uint)(SYNCHRONIZATION_ACCESS_RIGHTS.TIMER_MODIFY_STATE | SYNCHRONIZATION_ACCESS_RIGHTS.SYNCHRONIZATION_SYNCHRONIZE));
        if (_stopEvent.IsNull || _timer.IsNull)
        {
            ReleaseDuplication();
            CloseHandles();
            throw new InvalidOperationException("Could not create the Desktop Duplication thread's event or high-resolution timer.");
        }

        _duplication->GetDesc(out var desc);
        _thread = new Thread(Run) { Name = "Desktop Duplication", IsBackground = true };
        logger.LogInformation("Capturing monitor through Desktop Duplication at {Width}x{Height}, at most {Fps} fps",
            desc.ModeDesc.Width, desc.ModeDesc.Height, 2 * fps);
    }

    /// <summary>The monitor's output left the adapter; no more frames will arrive.</summary>
    public event Action? Closed;

    /// <summary>
    /// The frame handler threw (capture continues), or the duplication itself failed (its thread
    /// ends); the caller decides.
    /// </summary>
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
            ReleaseDuplication();
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
            long lastDelivered = 0;
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

                if (lastDelivered != 0 && !WaitUntil(lastDelivered + Interlocked.Read(ref _intervalQpc)))
                {
                    return;
                }

                DXGI_OUTDUPL_FRAME_INFO info;
                IDXGIResource* resource = null;
                var duplication = _duplication;
                var hr = ((delegate* unmanaged[Stdcall]<IDXGIOutputDuplication*, uint, DXGI_OUTDUPL_FRAME_INFO*, IDXGIResource**, HRESULT>)
                    (*(void***)duplication)[AcquireNextFrameSlot])(duplication, AcquireTimeoutMs, &info, &resource);
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
                try
                {
                    // A pointer-only update (no LastPresentTime) carries no new desktop image; nor does
                    // one no newer than the last. A duplication's first image may have been presented
                    // long before it was created: it is stamped no earlier than the creation.
                    var qpc = fresh ? Math.Max(info.LastPresentTime, _createdQpc) : info.LastPresentTime;
                    if (resource is not null && (fresh || info.LastPresentTime != 0) && qpc > lastQpc)
                    {
                        Deliver(qpc, resource);
                        lastQpc = qpc;
                        lastDelivered = Stopwatch.GetTimestamp();
                    }

                    fresh = false;
                }
                finally
                {
                    if (resource is not null) resource->Release();
                }

                hr = ((delegate* unmanaged[Stdcall]<IDXGIOutputDuplication*, HRESULT>)(*(void***)duplication)[ReleaseFrameSlot])(duplication);
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
            ReleaseDuplication();
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

    private void Deliver(long qpc, IDXGIResource* resource)
    {
        ID3D11Texture2D* texture = null;
        var textureIid = ID3D11Texture2D.IID_Guid;
        ((IUnknown*)resource)->QueryInterface(&textureIid, (void**)&texture).ThrowOnFailure();
        try
        {
            texture->GetDesc(out var desc);
            _onFrame(qpc, (nint)texture, new VideoSize((int)desc.Width, (int)desc.Height));
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

    /// <summary>
    /// Releases the duplication and creates it again, retrying every 100 ms while that fails; false
    /// when stopping, or when the monitor's output is gone (after raising <see cref="Closed"/>).
    /// </summary>
    private bool Recreate()
    {
        ReleaseDuplication();
        var logged = false;
        while (!_stopping)
        {
            var hr = TryDuplicate(out var duplication);
            if (hr.Succeeded)
            {
                _duplication = duplication;
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
            if (hr == DeviceRemoved || hr == DeviceReset)
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

    /// <summary>
    /// Duplicates the device adapter's output that shows the monitor; <c>DXGI_ERROR_NOT_FOUND</c>
    /// when the adapter has no such output, else the failure's HRESULT.
    /// </summary>
    private HRESULT TryDuplicate(out IDXGIOutputDuplication* duplication)
    {
        duplication = null;
        IDXGIDevice* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        IDXGIOutput1* output = null;
        try
        {
            var dxgiIid = IDXGIDevice.IID_Guid;
            var hr = ((IUnknown*)_device.Device)->QueryInterface(&dxgiIid, (void**)&dxgiDevice);
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
                output, (IUnknown*)_device.Device, &created);
            if (hr.Succeeded)
            {
                duplication = created;
                _createdQpc = Stopwatch.GetTimestamp();
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

    private void ReleaseDuplication()
    {
        if (_duplication is not null)
        {
            _duplication->Release();
            _duplication = null;
        }
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
}
