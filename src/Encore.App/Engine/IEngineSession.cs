using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;

namespace Encore.App.Engine;

/// <summary>What the App's views need from the engine: its state, and a way to send commands.</summary>
internal interface IEngineSession
{
    EngineStatus Status { get; }

    /// <summary>Why the last attempt to reach the engine failed, if it did.</summary>
    string? Problem { get; }

    /// <summary>The engine's state, or <c>null</c> before the first connection.</summary>
    StateSnapshot? State { get; }

    /// <summary>Raised, on a background thread, when <see cref="Status"/> or <see cref="State"/> changes.</summary>
    event Action? Changed;

    /// <summary>Sends a command; throws ErrorCodeException when the engine refuses it or there is no connection.</summary>
    Task<TResult> SendAsync<TArgs, TResult>(CommandType<TArgs, TResult> command, TArgs args, CancellationToken cancellationToken = default);
}
