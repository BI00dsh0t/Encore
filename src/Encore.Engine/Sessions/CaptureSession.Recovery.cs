using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Core.Sessions;
using Encore.Platform.Capture;
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
        JoinRestart();
        var attempt = new RecoveryAttempt();
        Exception? failure = null;
        try
        {
            RunAttempt(attempt, began);
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            // What this attempt built and didn't install; the ring stays for the next attempt.
            attempt.Capture?.Dispose();
            attempt.Next?.Dispose(keepRing: true);
            attempt.Device?.Dispose();
            if (!attempt.Installed)
            {
                lock (_chainLock)
                {
                    _recovering = false;
                }
            }
        }

        if (failure is not null)
        {
            ReportFailedAttempt(attempt, failure);
        }
    }

    /// <summary>The attempt itself: tear down once, build on a new device, install, then tell the orchestrator.</summary>
    private void RunAttempt(RecoveryAttempt attempt, long began)
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

        if (!ReadLost(out var lost, out var spec, out var window))
        {
            return;
        }

        if (_faults.Recovery() is { } fault)
        {
            throw fault;
        }

        BuildOnNewDevice(attempt, lost, spec, window);
        var now = Stopwatch.GetTimestamp();
        attempt.Next!.StartAudio(now);
        if (!InstallRecovered(attempt, window))
        {
            return;
        }

        var adapter = attempt.Device!.AdapterName;
        attempt.Device = null;
        attempt.Capture = null;
        attempt.Next = null;
        if (_options.GpuPriority is { } priority)
        {
            SetGpuPriority(priority);
        }

        _log.LogInformation("Recovered from the GPU device loss in {Ms:0} ms: a new device on {Adapter}, the encoder chain rebuilt; the frames captured meanwhile were dropped",
            (now - began) * 1000.0 / _qpcFrequency, adapter);
        _events.ChainRestarted(now);

        // The lost chain's Recording ends here, at its last packet, after the orchestrator was told.
        TakeLostTap()?.Dispose();
    }

    /// <summary>Reads the lost chain, the spec to rebuild and the window, under both locks; false when the session stopped.</summary>
    private bool ReadLost(out EncoderChain lost, out SessionSpec spec, out WindowInfo? window)
    {
        lock (_gate)
        {
            var stopped = _stopped;
            window = _window;
            lock (_chainLock)
            {
                lost = _current!;
                spec = _rebuildSpec ?? lost.Spec;
            }

            return !stopped;
        }
    }

    /// <summary>Builds the attempt's device, chain and capture; each is recorded as it is made, so a failure frees it.</summary>
    private void BuildOnNewDevice(RecoveryAttempt attempt, EncoderChain lost, SessionSpec spec, WindowInfo? window)
    {
        var device = _monitor is { } monitor
            ? D3D11Device.CreateForMonitor(monitor.Handle, _loggers.CreateLogger<D3D11Device>())
            : D3D11Device.Create(window!.Handle, _loggers.CreateLogger<D3D11Device>());
        attempt.Device = device;
        var (encoder, codec) = ReferenceEquals(spec, lost.Spec) ? (lost.EncoderName, lost.Codec) : _encoderResolver.Resolve(spec);
        var next = NewChain(spec, encoder, codec, null, device);
        attempt.Next = next;
        next.Subscribe();

        // The same ring in a new generation, so what it held before the reset stays saveable.
        var ring = HasRing && lost.Ring is { IsDisposed: false } kept ? kept : null;
        next.Build(_anchorQpc, first: false, latency: null, restartOf: ring, window: ring is null ? 0 : lost.WindowSeconds);
        if (IsMonitor)
        {
            // WGC, else Desktop Duplication, as at Open; started here and installed below.
            attempt.Capture = _sources.StartForMonitor(_composite, _monitor, device, next.Fps, c => c.Start());
        }
        else
        {
            var capture = _sources.ForWindow(window!.Handle, device, next.Fps);
            attempt.Capture = capture;
            capture.Start();
        }
    }

    /// <summary>Installs what the attempt built, under both locks; false, with nothing installed, when stopped or asleep.</summary>
    private bool InstallRecovered(RecoveryAttempt attempt, WindowInfo? window)
    {
        bool stopped;
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

                InstallRecoveredLocked(attempt);
            }
        }

        return !stopped && !asleep;
    }

    /// <summary>Makes the attempt's device, capture and chain the session's, and ends the lost state. Under the gate.</summary>
    private void InstallRecoveredLocked(RecoveryAttempt attempt)
    {
        lock (_chainLock)
        {
            _device = attempt.Device;
            _capture = attempt.Capture;
            _current = attempt.Next;
            _deviceLost = false;
            _tornDown = false;
            _recovering = false;
            _rebuildSpec = null;
            attempt.Installed = true;

            // A chain the profile asked for meanwhile is built on the new device.
            StartBuildLocked();
        }
    }

    /// <summary>Reports an attempt that threw: the orchestrator is told the device is still lost, unless the session stopped.</summary>
    private void ReportFailedAttempt(RecoveryAttempt attempt, Exception failure)
    {
        bool stopped;
        lock (_gate)
        {
            stopped = _stopped;
        }

        // Installed already (only closing the old Recording threw): the session has recovered.
        if (attempt.Installed)
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

    /// <summary>What one recovery attempt has built; whatever it did not install is freed when it ends.</summary>
    private sealed class RecoveryAttempt
    {
        public D3D11Device? Device { get; set; }

        public ICaptureSource? Capture { get; set; }

        public EncoderChain? Next { get; set; }

        public bool Installed { get; set; }
    }
}
