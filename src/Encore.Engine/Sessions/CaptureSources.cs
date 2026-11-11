using Microsoft.Extensions.Logging;
using Encore.Platform.Capture;

namespace Encore.Engine.Sessions;

/// <summary>Makes a session's capture sources, each wired to the session's frame, close and failure handlers.</summary>
internal sealed class CaptureSources(
    CaptureSessionOptions options,
    ICaptureFaults faults,
    ILoggerFactory loggers,
    ILogger log,
    CapturedFrameHandler onFrame,
    Action<ICaptureSource> onClosed,
    Action<Exception> onFailed)
{
    /// <summary>Which API the last capture made here goes through: <c>wgc</c>, <c>desktop-duplication</c> or a composite.</summary>
    public string Api { get; private set; } = "wgc";

    /// <summary>A game window's WGC capture, not started; a WGC failure throws, with no fallback.</summary>
    public WindowCapture ForWindow(nint window, D3D11Device device, int fps)
    {
        ThrowWgcFault();
        return Wire(new WindowCapture(device, window, fps, onFrame, loggers.CreateLogger<WindowCapture>()));
    }

    /// <summary>Starts the Monitor session's capture: the composite of several screens, else the one monitor's.</summary>
    public ICaptureSource StartForMonitor(
        IReadOnlyList<MonitorPlacement>? composite,
        MonitorInfo? monitor,
        D3D11Device device,
        int fps,
        Action<ICaptureSource> start) =>
        composite is not null ? StartComposite(composite, device, fps, start) : StartOne(monitor!, device, fps, start);

    /// <summary>The composited capture of the placements on <paramref name="device"/>, not started.</summary>
    public CompositeCapture Composite(IReadOnlyList<MonitorPlacement> placements, D3D11Device device, int fps)
    {
        var composite = Wire(new CompositeCapture(
            device, placements, fps,
            p => options.CompositeDuplication || faults.ForcesDuplication(p.Monitor.DeviceName),
            onFrame, loggers.CreateLogger<CompositeCapture>()));
        Api = options.CompositeDuplication ? "composite-desktop-duplication" : "composite";
        return composite;
    }

    /// <summary>Starts the composite; the fallback notice names each screen it captures through Desktop Duplication.</summary>
    private CompositeCapture StartComposite(
        IReadOnlyList<MonitorPlacement> placements, D3D11Device device, int fps, Action<ICaptureSource> start)
    {
        var composite = Composite(placements, device, fps);
        try
        {
            start(composite);
        }
        catch
        {
            composite.Dispose();
            throw;
        }

        var duplicated = composite.Sources
            .Where(s => s.Api == "desktop-duplication")
            .Select(s => s.Placement.Monitor.DeviceName)
            .ToList();
        if (duplicated.Count > 0)
        {
            options.CaptureFallback?.Invoke(
                $"Windows Graphics Capture couldn't capture {string.Join(", ", duplicated)}; "
                + "the Monitor Recording uses Desktop Duplication for it instead.");
        }

        return composite;
    }

    /// <summary>Starts one monitor's capture: WGC's monitor item, or Desktop Duplication when that throws.</summary>
    private ICaptureSource StartOne(MonitorInfo monitor, D3D11Device device, int fps, Action<ICaptureSource> start)
    {
        WindowCapture? wgc = null;
        try
        {
            ThrowWgcFault();
            wgc = Wire(WindowCapture.ForMonitor(device, monitor.Handle, fps, onFrame, loggers.CreateLogger<WindowCapture>()));
            start(wgc);
            Api = "wgc";
            return wgc;
        }
        catch (Exception e)
        {
            wgc?.Dispose();
            log.LogWarning(
                "WGC could not capture monitor {Device} ({Message}); falling back to Desktop Duplication", monitor.DeviceName, e.Message);
        }

        return StartDuplication(monitor, device, fps, start);
    }

    /// <summary>Starts the same monitor's capture through Desktop Duplication, and raises the fallback notice.</summary>
    private DesktopDuplicationCapture StartDuplication(MonitorInfo monitor, D3D11Device device, int fps, Action<ICaptureSource> start)
    {
        var duplication = Wire(new DesktopDuplicationCapture(
            device, monitor.Handle, fps, onFrame, loggers.CreateLogger<DesktopDuplicationCapture>()));
        try
        {
            start(duplication);
        }
        catch
        {
            duplication.Dispose();
            throw;
        }

        Api = "desktop-duplication";
        options.CaptureFallback?.Invoke(
            $"Windows Graphics Capture couldn't capture monitor {monitor.DeviceName}; "
            + "the Monitor Recording uses Desktop Duplication instead.");
        return duplication;
    }

    private void ThrowWgcFault()
    {
        if (faults.Wgc() is { } fault)
        {
            throw fault;
        }
    }

    private T Wire<T>(T capture)
        where T : ICaptureSource
    {
        capture.Closed += () => onClosed(capture);
        capture.Failed += onFailed;
        return capture;
    }
}
