using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Sessions;
using Encore.Platform.Capture;
using Encore.Platform.Windows;

namespace Encore.Engine.Sessions;

// The capture source: its frames, the anchor, a closed window and a retarget.
internal sealed partial class CaptureSession
{
    private void OnFrame(long qpc, nint texture, VideoSize contentSize)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (!_anchored)
            {
                StartOutput(qpc, contentSize);
            }

            // A lost device takes no frames: the recovery's new capture brings them again.
            if (_deviceLost)
            {
                return;
            }

            // For tests: a secure desktop delivers no frames; the pacer repeats the last one.
            if (_faults.DropsFrame())
            {
                return;
            }

            _stats?.Cadence.Record(qpc);
            _lastFrameQpc = qpc;
            if (_options.Stages == CaptureStages.Wgc)
            {
                return;
            }

            Feed(qpc, texture, contentSize);
        }
    }

    /// <summary>Gives one frame to the chains that are fed, switching to an armed chain first. Under the gate.</summary>
    private void Feed(long qpc, nint texture, VideoSize contentSize)
    {
        if (HasRing)
        {
            WatchSourceSize(qpc, contentSize);
        }

        // The first frame after an armed press: the new chain's first, and where the old chain's forced keyframe lands.
        if (_armed is { } armed && qpc > _armedQpc)
        {
            SwitchTo(armed, qpc);
        }

        if (_retiring is { Fed: true } retiring)
        {
            retiring.Draw(qpc, texture, contentSize);
        }

        // A forced restart drops the frames captured while it rebuilds the chain.
        if (_current is { Fed: true } current)
        {
            current.Draw(qpc, texture, contentSize);
        }
    }

    /// <summary>The first frame is the session anchor: every stream's PTS 0 is its QPC. Under the gate.</summary>
    private void StartOutput(long anchorQpc, VideoSize contentSize)
    {
        _anchorQpc = anchorQpc;
        var chain = _current!;
        _sourceSize = contentSize;
        SizedToSource(chain);
        _log.LogInformation("Anchor QPC {Anchor}; output {Size}, content {Content}", anchorQpc, chain.OutputSize, contentSize);

        _stats?.SnapshotAnchor();
        chain.Build(anchorQpc, first: true, _stats?.Latency);

        // The hub's streams have run since Open; each device takes blocks from here, cut once at the anchor.
        chain.StartAudio(anchorQpc);

        // A release write: SetMicMuted, Save and Record read the anchor without the gate.
        Volatile.Write(ref _anchored, true);

        // Only a session that can recover watches its device.
        if (HasRing || IsMonitor)
        {
            _deviceWatch = new Timer(_ => WatchDevice(), null, DeviceWatchInterval, DeviceWatchInterval);
        }

        // A chain asked for before the anchor is built from here.
        lock (_chainLock)
        {
            StartBuildLocked();
        }

        _options.Anchored?.Invoke(anchorQpc);

        // Only posts: Record can succeed from here.
        _events.Anchored(anchorQpc);
    }

    /// <summary>Makes the armed chain current at this frame; the old one is fed until its saves have cut. Under the gate.</summary>
    private void SwitchTo(EncoderChain armed, long qpc)
    {
        lock (_chainLock)
        {
            var old = _current!;
            _current = armed;
            _armed = null;
            CompleteMoveLocked(armed);
            _retiring = old;
            old.Retiring = true;
            old.Successor = armed;
            TakeRetirementLocked(old);
        }

        _log.LogInformation("The encoder chain switched at the frame {At:0.000} s after the anchor: {Size}, {Tracks} Tracks from here on",
            MicrosAt(qpc) / 1e6, armed.OutputSize, armed.Spec.Audio.Tracks.Count);
    }

    private void OnClosed(ICaptureSource capture)
    {
        lock (_gate)
        {
            // A window left by a retarget, or a session already stopping, doesn't end anything.
            if (_stopped || !ReferenceEquals(capture, _capture))
            {
                return;
            }

            if (_options.FollowWindow && !IsMonitor)
            {
                // The closed capture stays until a retarget or the end disposes it.
                _log.LogInformation("Window 0x{Handle:X} closed; the session waits for a retarget", _window?.Handle ?? 0);
            }
            else
            {
                RequestStop("window closed");
            }
        }

        _events.Closed("window closed");
    }

    public void Retarget(CaptureTarget target)
    {
        if (IsMonitor)
        {
            throw new InvalidOperationException("A Monitor session captures a monitor; it can't be retargeted.");
        }

        var window = WindowOfProcess(target);
        if (!TakeCaptureOut(target, window, out var old))
        {
            return;
        }

        // Disposing waits for a frame in flight, so the two captures never overlap frames.
        old?.Dispose();
        if (InstallCapture(target, window) is not { } next)
        {
            return;
        }

        next.Start();
        _log.LogInformation("Retargeted to window 0x{Handle:X}", target.Window);
    }

    /// <summary>The Target's window, which must belong to its process: the caller's pid is not trusted.</summary>
    private static WindowInfo WindowOfProcess(CaptureTarget target)
    {
        var window = WindowFinder.FromHandle(target.Window);
        if (window is null || (int)window.ProcessId != target.ProcessId)
        {
            throw new ErrorCodeException(
                ErrorCodes.RetargetOtherProcess,
                $"Window 0x{target.Window:X} does not belong to process {target.ProcessId}.");
        }

        return window;
    }

    /// <summary>Takes the running capture out, under the gate; false on a lost device, whose recovery captures the window.</summary>
    private bool TakeCaptureOut(CaptureTarget target, WindowInfo window, out ICaptureSource? old)
    {
        old = null;
        lock (_gate)
        {
            if (_stopped)
            {
                throw new ErrorCodeException(ErrorCodes.SessionNotRunning, "The session has already stopped.");
            }

            if (IsDeviceLost())
            {
                // No capture runs on a lost device: the recovery captures the new window.
                _window = window;
                _log.LogInformation(
                    "Retargeted to window 0x{Handle:X} while the GPU device is lost; the recovery captures it", target.Window);
                return false;
            }

            old = _capture;
            _capture = null;
        }

        return true;
    }

    /// <summary>Installs the new window's capture, under the gate; null when the session stopped or the device was lost.</summary>
    private WindowCapture? InstallCapture(CaptureTarget target, WindowInfo window)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return null;
            }

            if (IsDeviceLost() || _device is not { } device)
            {
                // Lost meanwhile: the recovery captures the new window.
                _window = window;
                return null;
            }

            // Built under the gate, so a recovery's teardown can't dispose the device under this capture.
            var next = _sources.ForWindow(target.Window, device, CaptureFps());
            _capture = next;
            _window = window;
            return next;
        }
    }

    private int CaptureFps()
    {
        lock (_chainLock)
        {
            return _current?.Fps ?? _spec.Snapshot.Video.Fps;
        }
    }

    /// <summary>Stops capturing a removed composited screen; its area turns black. Outside the gate.</summary>
    public bool DropMonitor(string device)
    {
        ICaptureSource? capture;
        lock (_gate)
        {
            capture = _capture;
        }

        return capture is CompositeCapture composite && composite.Drop(device);
    }
}
