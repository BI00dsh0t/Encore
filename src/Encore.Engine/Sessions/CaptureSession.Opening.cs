using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Sessions;
using Encore.Platform.Capture;
using Encore.Platform.Windows;
using VideoCodec = Encore.Contracts.Settings.VideoCodec;

namespace Encore.Engine.Sessions;

// Opening: what to capture, the device, the first chain and the capture.
internal sealed partial class CaptureSession
{
    /// <summary>Opens the device, subscribes to the audio devices and starts capture; the output opens on the first frame.</summary>
    public void Open()
    {
        PlanMonitorCanvas();

        // Before anything is opened: the first codec use may run the probe.
        var (encoder, codec) = _encoderResolver.Resolve(_spec);
        var target = _spec.Target;
        PickSource(target);
        AllocatePath();
        _device = (_monitor ?? _composite?[0].Monitor) is { } monitor
            ? D3D11Device.CreateForMonitor(monitor.Handle, _loggers.CreateLogger<D3D11Device>())
            : D3D11Device.Create(target.Window, _loggers.CreateLogger<D3D11Device>());
        if (_options.GpuPriority is { } priority)
        {
            SetGpuPriority(priority);
        }

        var first = InstallFirstChain(encoder, codec, _device);
        StartCapture(target, _device, first);
    }

    /// <summary>Several screens, or one screen matching its source: the plan sets codec and size before the encoder resolves.</summary>
    private void PlanMonitorCanvas()
    {
        if (!IsMonitor || _options.CompositeMonitors is not null)
        {
            return;
        }

        if (_spec.Screens is { Count: > 1 } screens)
        {
            PlanCanvas(screens);
            return;
        }

        if (!_spec.Snapshot.Video.MatchSource)
        {
            return;
        }

        var one = _spec.Screens is [var resolved]
            ? resolved
            : MonitorFinder.Find(_spec.Snapshot.MonitorRecordingMonitor, _log) is var found
                ? new DisplayMonitor(found.DeviceName, found.DeviceName, 0, 0, found.IsPrimary)
                : null;
        if (one is null)
        {
            return;
        }

        var placements = MonitorCanvas.Place([one]);
        if (placements.Count == 1)
        {
            (_spec, _) = CanvasPlanner.Plan(_spec, [one], placements, _encoderResolver.CanEncode, _options.Warn, _log);
        }
    }

    /// <summary>Picks what to capture: the composited screens, one monitor, the dev-only composite, or the Target's window.</summary>
    private void PickSource(CaptureTarget target)
    {
        if (_composite is not null)
        {
            _monitor = _composite[0].Monitor;
            _log.LogInformation(
                "Monitor capture of {Count} screens composited: {Screens}", _composite.Count, string.Join("; ", _composite));
        }
        else if (IsMonitor)
        {
            // One screen, captured directly: the resolved one, else the older single-monitor setting's (null is the primary).
            _monitor = MonitorFinder.Find(_spec.Screens is [var only] ? only.DeviceName : _spec.Snapshot.MonitorRecordingMonitor, _log);
            _log.LogInformation("Monitor capture of {Device}", _monitor.DeviceName);
        }
        else if (_options.CompositeMonitors is { } names)
        {
            // The dev-only composite: a file session of the monitors, on the primary composited monitor's adapter.
            _composite = MonitorCanvas.Place(names);
            if (_composite.Count == 0)
            {
                throw new EngineFailure(ErrorCodes.CaptureFailed, $"None of the monitors '{string.Join(", ", names)}' is attached.");
            }
        }
        else
        {
            _window = WindowFinder.FromHandle(target.Window);
        }
    }

    /// <summary>A session with no Rewind writes a file: the path given, else one allocated in the Output Folder.</summary>
    private void AllocatePath()
    {
        if (_options.Replay || IsMonitor)
        {
            return;
        }

        _path = _options.OutputPath is { } explicitPath
            ? Path.GetFullPath(explicitPath)
            : OutputNaming.Allocate(
                _spec.Snapshot.OutputFolder ?? _options.OutputFolder, _window?.Title ?? "Capture", DateTime.Now, File.Exists);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    /// <summary>Makes the first chain current; its taps hold the last second of blocks until the anchor starts its audio.</summary>
    private EncoderChain InstallFirstChain(string encoder, VideoCodec codec, D3D11Device device)
    {
        var first = NewChain(_spec, encoder, codec, _path, device);
        lock (_gate)
        {
            lock (_chainLock)
            {
                _current = first;
            }
        }

        first.Subscribe();
        _log.LogInformation("Mic {State} at start; mic mute covers {Inputs} Mic Device input(s) in muteAffected Tracks",
            _spec.MicMuted ? "muted" : "unmuted", _spec.Audio.GatedMicInputs().Count());
        return first;
    }

    /// <summary>Starts the capture and the first-frame timer; a Monitor capture is installed as it is chosen.</summary>
    private void StartCapture(CaptureTarget target, D3D11Device device, EncoderChain first)
    {
        if (IsMonitor)
        {
            // Started as it is chosen: a WGC item that fails to start falls back to Desktop Duplication.
            _firstFrameTimer.Change(FirstFrameTimeout, Timeout.InfiniteTimeSpan);
            _sources.StartForMonitor(_composite, _monitor, device, first.Fps, StartInstalled);
            return;
        }

        ICaptureSource capture = _composite is not null
            ? _sources.Composite(_composite, device, first.Fps)
            : _sources.ForWindow(target.Window, device, first.Fps);
        lock (_gate)
        {
            _capture = capture;
        }

        _firstFrameTimer.Change(FirstFrameTimeout, Timeout.InfiniteTimeSpan);
        capture.Start();
    }

    /// <summary>Plans the canvas of the screens; its codec and size replace the snapshot's video.</summary>
    private void PlanCanvas(IReadOnlyList<DisplayMonitor> screens)
    {
        (_spec, _composite) = CanvasPlanner.Plan(
            _spec, screens, MonitorCanvas.Place(screens), _encoderResolver.CanEncode, _options.Warn, _log);
    }

    /// <summary>Makes every chain of the session, at the snapshot's size or, when matching the source, the source's size.</summary>
    private EncoderChain NewChain(SessionSpec spec, string encoder, VideoCodec codec, string? path, D3D11Device device) =>
        SizedToSource(new EncoderChain(spec, encoder, codec, path, _options, device, _audio, _micMute, _loggers, _log, Fail));

    /// <summary>Sizes an unbuilt game chain from the game's captured size when the snapshot has <c>matchSource</c> on.</summary>
    private EncoderChain SizedToSource(EncoderChain chain) => chain.SizedToSource(IsMonitor, _movingTo ?? _sourceSize);

    /// <summary>Makes a capture the session's, then starts it, so a Closed it raises at once isn't taken for an old capture's.</summary>
    private void StartInstalled(ICaptureSource capture)
    {
        lock (_gate)
        {
            _capture = capture;
        }

        try
        {
            capture.Start();
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_capture, capture))
                {
                    _capture = null;
                }
            }

            throw;
        }
    }

    /// <summary>Sets the device's GPU thread priority once; a failure is logged and the run goes on.</summary>
    private void SetGpuPriority(int priority)
    {
        int hr;
        int? readBack;
        try
        {
            hr = _device!.SetGpuThreadPriority(priority, out readBack);
        }
        catch (Exception e)
        {
            hr = e.HResult;
            readBack = null;
        }

        var hex = $"0x{hr:X8}";
        var succeeded = hr >= 0;
        if (succeeded)
        {
            _log.LogInformation("GPU thread priority set to {Priority} ({Hresult}); device reports {ReadBack}", priority, hex, readBack);
        }
        else
        {
            _log.LogWarning("{Code}: SetGPUThreadPriority({Priority}) failed with {Hresult}; capturing without it",
                ErrorCodes.GpuPriorityFailed, priority, hex);
        }

        _stats?.GpuPriority = new GpuPriorityReport(priority, hex, succeeded, readBack);
    }

    private void OnFirstFrameTimeout()
    {
        lock (_gate)
        {
            if (_anchored || _stopped)
            {
                return;
            }
        }

        Fail(new EngineFailure(ErrorCodes.CaptureFailed,
            $"No frame arrived from the window within {FirstFrameTimeout.TotalSeconds:0} s."));
    }

    /// <summary>Frees what <see cref="Open"/> managed to open, when it threw.</summary>
    public void Abandon()
    {
        ICaptureSource? capture;
        Timer? watch;
        lock (_gate)
        {
            _stopped = true;
            capture = _capture;
            _capture = null;
            watch = _deviceWatch;
            _deviceWatch = null;
        }

        _firstFrameTimer.Dispose();
        watch?.Dispose();
        capture?.Dispose();
        DisposeAll();
    }
}
