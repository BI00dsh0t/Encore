using Encore.Contracts.Ipc;

namespace Encore.Engine.Ipc;

/// <summary>The writer queue's limits; tests shorten them.</summary>
internal sealed record IpcServerOptions
{
    public int MaxQueued { get; init; } = 1000;

    public TimeSpan MaxLag { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxFrameBytes { get; init; } = Protocol.MaxEngineFrameBytes;

    /// <summary>How long a shutdown waits for queued frames to be written.</summary>
    public TimeSpan Drain { get; init; } = TimeSpan.FromSeconds(1);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}
