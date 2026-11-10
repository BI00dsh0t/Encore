namespace Encore.Core.Sessions;

/// <summary>Starts capture sessions. Called only on the orchestrator thread.</summary>
public interface ICaptureSessionFactory
{
    /// <summary>Starts a session for a spec; it reports back only through events, and a throw means nothing was started.</summary>
    ICaptureSession Start(SessionSpec spec, ISessionEvents events);
}
