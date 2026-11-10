using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;

namespace Encore.Platform.Capture;

/// <summary>Desktop Duplication of one monitor: the fallback when WGC can't capture it, never for games. docs/code-notes.md.</summary>
public sealed class DesktopDuplicationCapture : CaptureSource
{
    private const uint RetryMs = 100;

    private readonly IOutputDuplication _duplication;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly Lock _gate = new();
    private readonly Thread _thread;
    private readonly StoppableTimer _clock;
    private long _intervalQpc;
    private long _createdQpc;
    private int _simulateLost;
    private volatile bool _stopping;
    private bool _started;
    private bool _exited;
    private bool _disposed;

    /// <summary>Frames are delivered at most 2 × fps a second; throws when the monitor isn't an output of the device's adapter.</summary>
    public DesktopDuplicationCapture(D3D11Device device, nint hmonitor, int fps, CapturedFrameHandler onFrame, ILogger logger)
        : this(
            new DxgiOutputDuplication(device ?? throw new ArgumentNullException(nameof(device)), ValidMonitor(hmonitor)),
            fps,
            onFrame,
            logger)
    {
    }

    /// <summary>For tests: captures through <paramref name="duplication"/>, a fake of DXGI's.</summary>
    internal DesktopDuplicationCapture(IOutputDuplication duplication, int fps, CapturedFrameHandler onFrame, ILogger logger)
        : base(onFrame, logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        _duplication = duplication;
        _intervalQpc = HalfSlot(_qpcFrequency, fps);

        var result = duplication.Duplicate();
        if (result == HRESULT.DXGI_ERROR_NOT_FOUND)
        {
            throw new InvalidOperationException("The monitor is not an output of the capture device's adapter.");
        }

        result.ThrowOnFailure();
        _createdQpc = Stopwatch.GetTimestamp();

        var clock = StoppableTimer.TryCreate();
        if (clock is null)
        {
            duplication.Release();
            throw new InvalidOperationException("Could not create the Desktop Duplication thread's event or high-resolution timer.");
        }

        _clock = clock;
        var size = duplication.ModeSize;
        _thread = new Thread(Run) { Name = "Desktop Duplication", IsBackground = true };
        logger.LogInformation("Capturing monitor through Desktop Duplication at {Width}x{Height}, at most {Fps} fps",
            size.Width, size.Height, 2 * fps);
    }

    /// <summary>Where in the loop the duplication's access was lost, for its log line.</summary>
    private enum LossPoint
    {
        Simulated,
        Acquire,
        Release,
    }

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

        _thread.Start();
    }

    /// <summary>Delivers at most <c>2 × <paramref name="fps"/></c> frames a second from the next frame on.</summary>
    public override void SetFps(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        Interlocked.Exchange(ref _intervalQpc, HalfSlot(_qpcFrequency, fps));
        LogRate(fps);
    }

    /// <summary>For tests: the next acquire behaves as if it returned <c>DXGI_ERROR_ACCESS_LOST</c>.</summary>
    internal void SimulateAccessLost() => Interlocked.Exchange(ref _simulateLost, 1);

    /// <summary>Stops the thread, waiting for a frame in flight to finish, and releases the duplication.</summary>
    public override void Dispose()
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
                _clock.Stop();
            }
        }

        if (!started)
        {
            _duplication.Release();
            _clock.Dispose();
        }
        else if (Thread.CurrentThread != _thread)
        {
            // The thread releases the duplication as it exits.
            _thread.Join();
            _clock.Dispose();
        }

        // Called from the thread itself (a frame handler ending the session), it closes the handles as it exits.
    }

    private static nint ValidMonitor(nint hmonitor) =>
        hmonitor == 0 ? throw new ArgumentException("No monitor was given.", nameof(hmonitor)) : hmonitor;

    private void Run()
    {
        try
        {
            Poll();
        }
        catch (Exception e)
        {
            // Never take the process down from a capture thread: Failed tells the session, which fails.
            Logger.LogError(e, "Desktop Duplication thread failed");
            RaiseFailed(e);
        }
        finally
        {
            _duplication.Release();
            lock (_gate)
            {
                _exited = true;
                if (_disposed)
                {
                    _clock.Dispose();
                }
            }
        }
    }

    /// <summary>Polls every half slot until stopped; returns when stopping or when the duplication can't come back.</summary>
    private void Poll()
    {
        long nextPoll = 0;
        long lastQpc = 0;

        // The first frame of a duplication is the whole desktop, delivered even when static.
        var fresh = true;
        while (!_stopping)
        {
            if (Interlocked.Exchange(ref _simulateLost, 0) == 1)
            {
                if (!RecreateAfterLoss(LossPoint.Simulated))
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
            var result = _duplication.TryAcquire(out var frame);
            nextPoll = Stopwatch.GetTimestamp() + Interlocked.Read(ref _intervalQpc);
            if (result == HRESULT.DXGI_ERROR_WAIT_TIMEOUT)
            {
                continue;
            }

            if (result == HRESULT.DXGI_ERROR_ACCESS_LOST)
            {
                if (!RecreateAfterLoss(LossPoint.Acquire))
                {
                    return;
                }

                fresh = true;
                continue;
            }

            // Any other failure (a removed device, say) can't be recreated: it fails the source.
            result.ThrowOnFailure();
            lastQpc = DeliverIfNew(frame, fresh, lastQpc);
            fresh = false;
            result = _duplication.ReleaseFrame();
            if (result == HRESULT.DXGI_ERROR_ACCESS_LOST && !_stopping)
            {
                if (!RecreateAfterLoss(LossPoint.Release))
                {
                    return;
                }

                fresh = true;
            }
            else if (!_stopping)
            {
                result.ThrowOnFailure();
            }
        }
    }

    /// <summary>Delivers a frame that carries a new image; returns the stamp of the last frame delivered.</summary>
    private long DeliverIfNew(DuplicatedFrame frame, bool fresh, long lastQpc)
    {
        // A pointer-only or stale update carries no new image; a first image is stamped no earlier than the creation.
        var qpc = fresh ? Math.Max(frame.LastPresentTime, _createdQpc) : frame.LastPresentTime;
        if (frame.Texture == 0 || (!fresh && frame.LastPresentTime == 0) || qpc <= lastQpc)
        {
            return lastQpc;
        }

        Deliver(qpc, frame.Texture, frame.Size);
        return qpc;
    }

    private bool RecreateAfterLoss(LossPoint point)
    {
        switch (point)
        {
            case LossPoint.Simulated:
                Logger.LogInformation("Desktop Duplication access lost (simulated)");
                break;
            case LossPoint.Acquire:
                Logger.LogInformation("Desktop Duplication access lost; recreating it");
                break;
            default:
                Logger.LogInformation("Desktop Duplication access lost at a frame release; recreating it");
                break;
        }

        return Recreate();
    }

    /// <summary>Recreates the duplication, retrying every 100 ms; false when stopping or when the monitor's output is gone.</summary>
    private bool Recreate()
    {
        _duplication.Release();
        var logged = false;
        while (!_stopping)
        {
            var result = _duplication.Duplicate();
            if (result.Succeeded)
            {
                _createdQpc = Stopwatch.GetTimestamp();
                Logger.LogInformation("Desktop Duplication recreated");
                return true;
            }

            if (result == HRESULT.DXGI_ERROR_NOT_FOUND)
            {
                RaiseOutputGone();
                return false;
            }

            // A removed or reset device never duplicates again: it fails the source.
            if (result == DxgiOutputDuplication.DeviceRemoved || result == DxgiOutputDuplication.DeviceReset)
            {
                result.ThrowOnFailure();
            }

            if (!logged)
            {
                Logger.LogInformation(
                    "Desktop Duplication could not be recreated yet (0x{HResult:X8}); retrying every {Ms} ms", result.Value, RetryMs);
                logged = true;
            }

            if (_clock.WaitForStop(RetryMs))
            {
                return false;
            }
        }

        return false;
    }

    private void RaiseOutputGone()
    {
        if (TryClaimClose())
        {
            Logger.LogInformation("Captured monitor closed (its output is gone)");
            RaiseClosed();
        }
    }

    /// <summary>Sleeps until <paramref name="deadline"/> (QPC); false when stopping.</summary>
    private bool WaitUntil(long deadline) => _clock.WaitUntil(deadline) && !_stopping;
}
