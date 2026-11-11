namespace Encore.App.Engine;

/// <summary>How connecting to the engine, launching it if need be, ended.</summary>
internal enum ConnectOutcome
{
    Connected,
    Failed,

    /// <summary>A live engine didn't take a new connection within the reply limit.</summary>
    Unanswered,
}
