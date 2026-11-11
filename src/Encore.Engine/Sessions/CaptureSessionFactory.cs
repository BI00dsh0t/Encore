using Microsoft.Extensions.Logging;
using Encore.Core.Audio;
using Encore.Core.Sessions;
using Encore.Platform.FFmpeg;

namespace Encore.Engine.Sessions;

/// <summary>Starts capture sessions with one set of options, the engine's audio hub and one encoder catalog.</summary>
internal sealed class CaptureSessionFactory(CaptureSessionOptions options, ILoggerFactory loggers, IAudioDeviceHub audio, IVideoEncoderCatalog? encoders = null)
    : ICaptureSessionFactory
{
    private readonly IVideoEncoderCatalog _encoders = encoders ?? new VideoEncoderCatalog(loggers);

    public ICaptureSession Start(SessionSpec spec, ISessionEvents events)
    {
        FFmpegLibrary.Initialize(loggers.CreateLogger("FFmpeg"));
        var session = new CaptureSession(spec, events, options, audio, _encoders, loggers);
        try
        {
            session.Open();
        }
        catch
        {
            session.Abandon();
            throw;
        }

        return session;
    }
}
