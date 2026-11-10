using System.Diagnostics;
using Encore.Contracts.Ipc;

namespace Encore.App.Engine;

/// <summary>One connection: its client, the verified engine process behind it, and its watchdog.</summary>
internal sealed record EngineLink(EngineClient Client, Process Engine, EngineWatchdog Watchdog) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        Engine.Dispose();
    }
}
