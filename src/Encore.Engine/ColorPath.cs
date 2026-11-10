namespace Encore.Engine;

/// <summary>How captured BGRA reaches NVENC.</summary>
public enum ColorPath
{
    /// <summary>BGRA straight into NVENC, which converts in hardware.</summary>
    X1,

    /// <summary>BGRA → NV12 on the D3D11 VideoProcessor, NV12 into NVENC.</summary>
    X2,
}
