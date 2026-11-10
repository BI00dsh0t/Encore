using Encore.Contracts.Messages;

namespace Encore.Engine;

/// <summary>A parsed engine command.</summary>
public abstract record EngineCommand;

/// <summary><c>debug-list</c>: windows and audio endpoints.</summary>
public sealed record DebugListCommand : EngineCommand;

/// <summary>The private media worker, isolated from live engine startup.</summary>
public sealed record ClipMediaWorkerCommand : EngineCommand;

/// <c>serve</c>: the engine proper, serving its pipe until <c>engine.shutdown</c> or Ctrl+C. Flags: docs/code-notes.md.
public sealed record ServeCommand(
    string? Instance,
    bool DebugCapture = false,
    bool Rewind = false,
    EngineRestartReason? Restarted = null) : EngineCommand
{
    /// <summary>The only instance names <c>--instance</c> accepts, so tests never touch a real instance.</summary>
    public const string TestInstancePrefix = Contracts.Instance.TestPrefix;
}

/// <summary>The outcome of parsing: a command, or the reason there isn't one.</summary>
public sealed record ParseResult(EngineCommand? Command, string? Error);
