namespace Encore.App.Engine;

/// <summary>Where the App stands with the engine.</summary>
internal enum EngineStatus
{
    Connecting,
    Connected,

    /// <summary>The engine crashed or hung, and the App is starting it again.</summary>
    Restarting,
    NotRunning,
    Disconnected,

    /// <summary>The engine crashed or hung too often: the App stopped relaunching it until the user restarts it.</summary>
    CrashLoop,
}
