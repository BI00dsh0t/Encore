using Encore.Contracts.Messages;

namespace Encore.App.Engine;

/// <summary>The views' one rule for a session's state: connected, or nothing.</summary>
internal static class EngineSessions
{
    /// <summary>The session's state while it is <see cref="EngineStatus.Connected"/>, else <c>null</c>.</summary>
    public static StateSnapshot? ConnectedState(this IEngineSession engine) => ConnectedState(engine.Status, engine.State);

    /// <summary>The state when the status is <see cref="EngineStatus.Connected"/>, else <c>null</c>.</summary>
    public static StateSnapshot? ConnectedState(EngineStatus status, StateSnapshot? state) =>
        status == EngineStatus.Connected ? state : null;
}
