namespace Encore.Engine.Sessions;

/// <summary>The faults a test injects into a capture session; the session asks at each point where one could happen.</summary>
internal interface ICaptureFaults
{
    /// <summary>How long a size move's built chain waits before it arms.</summary>
    TimeSpan MoveArmDelay { get; }

    /// <summary>Asked on the writer thread just before a Clip is written; a non-null result is thrown.</summary>
    Exception? ClipWrite(string path);

    /// <summary>Asked just before a restart builds the new chain; a non-null result is thrown there.</summary>
    Exception? Restart();

    /// <summary>Asked at the start of each GPU-reset recovery attempt; a non-null result is thrown there.</summary>
    Exception? Recovery();

    /// <summary>Asked before any WGC capture is created; a non-null result is thrown, as WGC failing.</summary>
    Exception? Wgc();

    /// <summary>Asked for each frame: true drops it, as on the secure desktop, where WGC delivers none.</summary>
    bool DropsFrame();

    /// <summary>Whether a composited screen, by GDI device name, is captured through Desktop Duplication instead of WGC.</summary>
    bool ForcesDuplication(string device);
}
