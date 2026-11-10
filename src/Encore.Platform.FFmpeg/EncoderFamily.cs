namespace Encore.Platform.FFmpeg;

/// <summary>The kind of an FFmpeg video encoder, which decides its rate-control options.</summary>
public enum EncoderFamily
{
    Nvenc,
    Amf,
    Qsv,
    X264,
    Other,
}
