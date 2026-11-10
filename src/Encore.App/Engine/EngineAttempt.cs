namespace Encore.App.Engine;

/// <summary>How one attempt to connect to the engine's pipe ended.</summary>
internal enum EngineAttempt
{
    Connected,

    /// <summary>Something answered on the pipe that isn't the trusted engine image.</summary>
    Untrusted,
    NoAnswer,
}
