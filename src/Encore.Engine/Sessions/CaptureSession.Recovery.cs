using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Core.Sessions;
using Encore.Platform.Capture;
using Encore.Platform.FFmpeg;
using Encore.Platform.Windows;

namespace Encore.Engine.Sessions;

// Recovering from a lost GPU device: a new device, chain and capture.
internal sealed partial class CaptureSession
{
    /// <summary>Starts one attempt to recover from a GPU device loss, on its own thread. Orchestrator thread; never blocks.</summary>
    public bool RecoverDevice()
    {
        lock (_gate)
        {
            lock (_chainLock)
            {
                if (!_deviceLost || _recovering || _stopped || _chainsClosed)
                {
                    return false;
                }

                _recovering = true;
                _suspended = false;
                var previous = _recoveryThread;
                _recoveryThread = new Thread(() => Recover(previous)) { Name = "GPU device recovery", IsBackground = true };
                _recoveryThread.Start();
            }
        }

        return true;
    }

    /// <summary>One recovery attempt, on its own thread: a new device, chain and capture. Order: docs/code-notes.md.</summary>
    private void Recover(Thread? previous)
    {
        previous?.Join();
        var began = Stopwatch.GetTimestamp();
        Thread? restart;
        lock (_chainLock)
        {
            restart = _restartThread;
        }

        restart?.Join();
        D3D11Device? device = null;
        ICaptureSource? capture = null;
        EncoderChain? next = null;
        var installed = false;
        var stopped = false;
        Exception? failure = null;
        try
        {
            bool tornDown;
            lock (_chainLock)
            {
                tornDown = _tornDown;
            }

            if (!tornDown)
            {
                TearDownLost();
            }

            EncoderChain lost;
            SessionSpec spec;
            WindowInfo? window;
            lock (_gate)
            {
                stopped = _stopped;
                window = _window;
                lock (_chainLock)
                {
                    lost = _current!;
                    spec = _rebuildSpec ?? lost.Spec;
                }
            }

            if (stopped)
            {
                return;
            }

            if (_options.RecoveryFault?.Invoke() is { } fault)
            {
                throw fault;
            }

            device = _monitor is { } monitor
                ? D3D11Device.CreateForMonitor(monitor.Handle, _loggers.CreateLogger<D3D11Device>())
                : D3D11Device.Create(window!.Handle, _loggers.CreateLogger<D3D11Device>());
            var (encoder, codec) = ReferenceEquals(spec, lost.Spec) ? (lost.EncoderName, lost.Codec) : _encoderResolver.Resolve(spec);
            next = NewChain(spec, encoder, codec, null, device);
            next.Subscribe();

            // The same ring in a new generation, so what it held before the reset stays saveable.
            var ring = HasRing && lost.Ring is { IsDisposed: false } kept ? kept : null;
            next.Build(_anchorQpc, first: false, latency: null, restartOf: ring, window: ring is null ? 0 : lost.WindowSeconds);
            if (IsMonitor)
            {
                // WGC, else Desktop Duplication, as at Open; started here and installed below.
                capture = _sources.StartForMonitor(_composite, _monitor, device, next.Fps, c => c.Start());
            }
            else
            {
                capture = _sources.ForWindow(window!.Handle, device, next.Fps);
                capture.Start();
            }
            var now = Stopwatch.GetTimestamp();
            next.StartAudio(now);
            var asleep = false;
            lock (_gate)
            {
                stopped = _stopped;
                lock (_chainLock)
                {
                    asleep = _suspended;
                }

                if (asleep && !stopped)
                {
                    _log.LogInformation("The PC went to sleep during the recovery attempt: nothing is installed; the resume rebuilds");
                }

                if (!stopped && !asleep)
                {
                    if (!ReferenceEquals(_window, window))
                    {
                        throw new InvalidOperationException("The Target's window changed during the recovery; the next attempt captures the new one.");
                    }

                    lock (_chainLock)
                    {
                        _device = device;
                        _capture = capture;
                        _current = next;
                        _deviceLost = false;
                        _tornDown = false;
                        _recovering = false;
                        _rebuildSpec = null;
                        installed = true;

                        // A chain the profile asked for meanwhile is built on the new device.
                        StartBuildLocked();
                    }
                }
            }

            if (stopped || asleep)
            {
                return;
            }

            var adapter = device.AdapterName;
            device = null;
            capture = null;
            next = null;
            if (_options.GpuPriority is { } priority)
            {
                SetGpuPriority(priority);
            }

            _log.LogInformation("Recovered from the GPU device loss in {Ms:0} ms: a new device on {Adapter}, the encoder chain rebuilt; the frames captured meanwhile were dropped",
                (now - began) * 1000.0 / _qpcFrequency, adapter);
            _events.ChainRestarted(now);

            // The lost chain's Recording ends here, at its last packet, after the orchestrator was told.
            RecordingTap? tap;
            lock (_chainLock)
            {
                tap = _lostTap;
                _lostTap = null;
            }

            tap?.Dispose();
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            // What this attempt built and didn't install; the ring stays for the next attempt.
            capture?.Dispose();
            next?.Dispose(keepRing: true);
            device?.Dispose();
            if (!installed)
            {
                lock (_chainLock)
                {
                    _recovering = false;
                }
            }
        }

        if (failure is null)
        {
            return;
        }

        lock (_gate)
        {
            stopped = _stopped;
        }

        // Installed already (only closing the old Recording threw): the session has recovered.
        if (installed)
        {
            _log.LogWarning("Ending the lost chain's Recording after the GPU recovery failed: {Message}", failure.Message);
            return;
        }

        _log.LogWarning("The recovery from the GPU device loss failed: {Message}", failure.Message);
        if (!stopped)
        {
            _events.DeviceLost(failure);
        }
    }
}
