using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Platform.Capture;
using Encore.Platform.FFmpeg;

namespace Encore.Engine.Sessions;

// A lost GPU device and sleep: noticing the loss, and tearing down what ran on the device.
internal sealed partial class CaptureSession
{
    /// <summary>The device watchdog's tick: a device that reports itself removed or reset counts as a loss.</summary>
    private void WatchDevice()
    {
        int reason;
        lock (_gate)
        {
            if (_stopped || _device is not { } device || IsDeviceLost())
            {
                return;
            }

            reason = device.RemovedReason;
        }

        if (reason < 0)
        {
            Fail(new COMException($"The GPU device was removed or reset (0x{reason:X8}).", reason));
        }
    }

    /// <summary>Takes an exception as a GPU device loss when it is one and the session can recover; false leaves it to Fail.</summary>
    private bool TakeDeviceLoss(Exception e)
    {
        lock (_gate)
        {
            lock (_chainLock)
            {
                if (_deviceLost)
                {
                    _log.LogInformation("A failure on the lost GPU device was ignored: {Message}", e.Message);
                    return true;
                }

                if (!Volatile.Read(ref _anchored) || !(HasRing || IsMonitor) || _stopped || _outputFinished || _keepRing || _chainsClosed)
                {
                    return false;
                }
            }

            if (!IsDeviceLoss(e))
            {
                return false;
            }

            lock (_chainLock)
            {
                _deviceLost = true;
                DropMoveLocked();
                foreach (var chain in new[] { _current, _armed, _retiring })
                {
                    chain?.Fed = false;
                }
            }
        }

        _log.LogWarning("{Code}: the GPU device was lost ({Message}); nothing is encoded until the session recovers", ErrorCodes.GpuReset, e.Message);
        _events.DeviceLost(e);
        return true;
    }

    /// <summary>Whether an exception is a device loss: a removed or reset HRESULT, or a device that says it is lost.</summary>
    private bool IsDeviceLoss(Exception e)
    {
        for (var inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner.HResult is D3D11Device.DeviceRemoved or D3D11Device.DeviceReset)
            {
                return true;
            }
        }

        return _device?.IsLost ?? false;
    }

    /// <summary>Whether the device is lost; under the gate.</summary>
    private bool IsDeviceLost()
    {
        lock (_chainLock)
        {
            return _deviceLost;
        }
    }

    /// <summary>Whether the device is lost; takes the gate.</summary>
    private bool IsDeviceLostNow()
    {
        lock (_gate)
        {
            return IsDeviceLost();
        }
    }

    /// <summary>The PC is going to sleep: tears down as for a lost device, so the resume rebuilds. Never blocks.</summary>
    public bool Suspend()
    {
        lock (_gate)
        {
            lock (_chainLock)
            {
                if (_suspended)
                {
                    return true;
                }

                if (!Volatile.Read(ref _anchored) || !(HasRing || IsMonitor) || _stopped || _outputFinished || _keepRing || _chainsClosed)
                {
                    return false;
                }

                // Already lost: a running attempt installs nothing now, and the teardown below runs once it has finished.
                _suspended = true;
                _deviceLost = true;
                DropMoveLocked();
                foreach (var chain in new[] { _current, _armed, _retiring })
                {
                    chain?.Fed = false;
                }

                _log.LogInformation("The PC is going to sleep: tearing capture, the encoder chains and the device down; Rewind's ring is kept");
                var previous = _recoveryThread;
                _recoveryThread = new Thread(() => SuspendTearDown(previous)) { Name = "Suspend teardown", IsBackground = true };
                _recoveryThread.Start();
            }
        }

        return true;
    }

    /// <summary>Suspend's thread: the last attempt and a forced restart finish, then the teardown, then the tap ends.</summary>
    private void SuspendTearDown(Thread? previous)
    {
        try
        {
            previous?.Join();
            Thread? restart;
            bool tornDown;
            lock (_chainLock)
            {
                restart = _restartThread;
            }

            restart?.Join();
            lock (_chainLock)
            {
                tornDown = _tornDown;
            }

            if (!tornDown && !Volatile.Read(ref _stopped))
            {
                TearDownLost();
            }
        }
        catch (Exception e)
        {
            _log.LogWarning("Tearing down for sleep failed: {Message}", e.Message);
        }

        // The Recording ends here, at its last fragment; a resume never continues it.
        RecordingTap? tap;
        lock (_chainLock)
        {
            tap = _lostTap;
            _lostTap = null;
        }

        try
        {
            tap?.Dispose();
        }
        catch (Exception e)
        {
            _log.LogWarning("Ending the Recording for sleep failed: {Message}", e.Message);
        }
    }

    /// <summary>The first recovery attempt's teardown, in order: WGC, then every chain (the ring kept), then the device.</summary>
    private void TearDownLost()
    {
        var began = Stopwatch.GetTimestamp();
        ICaptureSource? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }

        // Disposing waits for a frame in flight, which takes the gate: never under it.
        capture?.Dispose();
        EncoderChain? armed;
        EncoderChain? retiring;
        EncoderChain? prepared;
        lock (_gate)
        {
            lock (_chainLock)
            {
                armed = _armed;
                _armed = null;
                retiring = _retiring;
                _retiring = null;
                prepared = _prepared;
                _prepared = null;
                _rebuildSpec = armed?.Spec ?? _rebuildSpec ?? _current!.Spec;
            }
        }

        // Builds, teardowns and retirements already running finish; none starts while the device is lost.
        while (true)
        {
            List<Thread> workers;
            lock (_chainLock)
            {
                workers = [.. _workers];
                _workers.Clear();
            }

            if (workers.Count == 0)
            {
                break;
            }

            foreach (var worker in workers)
            {
                worker.Join();
            }
        }

        EncoderChain current;
        RecordingTap? extra = null;
        EncoderChain? built;
        lock (_chainLock)
        {
            // A build that finished meanwhile.
            built = _prepared;
            _prepared = null;
            current = _current!;
            var tap = current.TakeTap();
            if (_lostTap is null)
            {
                _lostTap = tap;
            }
            else
            {
                extra = tap;
            }
        }

        try
        {
            extra?.Dispose();
        }
        catch (Exception e)
        {
            _log.LogWarning("Closing a second lost Recording tap failed: {Message}", e.Message);
        }

        // Each save bound to it cuts at its press, since its keyframe never comes; the ring's restart needs them done.
        var deadline = began + (3 * _qpcFrequency);
        while (!Volatile.Read(ref _stopped))
        {
            lock (_chainLock)
            {
                if (current.Writers == 0)
                {
                    break;
                }
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                _log.LogWarning("A save bound to the lost encoder chain hadn't cut within 3 s; the chain is torn down anyway");
                break;
            }

            Thread.Sleep(10);
        }

        // Each in its own step: one that throws on the lost device never leaks the others.
        void Step(string what, Action dispose)
        {
            try
            {
                dispose();
            }
            catch (Exception e)
            {
                _log.LogWarning("Tearing down the lost {What} failed: {Message}", what, e.Message);
            }
        }

        Step("encoder chain", () => current.Dispose(keepRing: true));
        Step("armed encoder chain", () => armed?.Dispose());
        Step("replaced encoder chain", () => retiring?.Dispose());
        Step("prepared encoder chain", () => prepared?.Dispose());
        Step("built encoder chain", () => built?.Dispose());
        D3D11Device? device;
        lock (_gate)
        {
            device = _device;
            _device = null;
        }

        Step("device", () => device?.Dispose());
        lock (_chainLock)
        {
            _tornDown = true;
        }

        _log.LogInformation("The lost GPU device, its capture and its encoder chains were torn down in {Ms:0} ms; Rewind's ring is kept",
            (Stopwatch.GetTimestamp() - began) * 1000.0 / _qpcFrequency);
    }
}
