namespace ReplayBuffer.Platform.Capture;

/// <summary>
/// A frame source a capture session draws from: WGC's <see cref="WindowCapture"/>, or, for a
/// Monitor Recording whose WGC item fails, <see cref="DesktopDuplicationCapture"/> (AD-12). Frames
/// reach the <see cref="CapturedFrameHandler"/> given at construction, one at a time.
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>The captured window or monitor is gone; no more frames will arrive.</summary>
    event Action? Closed;

    /// <summary>The frame handler threw, or the source itself failed; the caller decides (the session fails).</summary>
    event Action<Exception>? Failed;

    /// <summary>Starts delivering frames.</summary>
    void Start();

    /// <summary>Sets the output frame rate: frames arrive at most twice a slot of <paramref name="fps"/>.</summary>
    void SetFps(int fps);
}
