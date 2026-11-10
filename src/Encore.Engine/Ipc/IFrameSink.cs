namespace Encore.Engine.Ipc;

/// <summary>Where the hub delivers frames: one per connection.</summary>
internal interface IFrameSink
{
    /// <summary>Queues a frame without blocking; a sink that can't take it drops itself.</summary>
    void Enqueue(byte[] frame, string? coalesceKey);
}
