namespace Encore.Engine;

/// <summary>How much of the video path a <c>debug-capture</c> runs, to split its cost.</summary>
public enum CaptureStages
{
    /// <summary>WGC → copy → NVENC → file: the tracer.</summary>
    Full,

    /// <summary>WGC → copy into the encoder's pool; nothing is encoded.</summary>
    Copy,

    /// <summary>WGC frames are taken and released; no copy, no encode.</summary>
    Wgc,
}
