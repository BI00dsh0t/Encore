using System.Collections.Concurrent;
using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>The packets the encoders hand a Recording's writer: cloned on their threads, taken on the writer's own.</summary>
internal sealed unsafe class RecordingQueue : IDisposable
{
    private readonly ConcurrentQueue<(nint Packet, int Stream)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Lock _lock = new();
    private bool _closed;
    private volatile bool _cloneFailed;

    /// <summary>The packets offered and not yet taken.</summary>
    public int Count => _queue.Count;

    /// <summary>Whether a clone failed, so the Recording lost a packet.</summary>
    public bool CloneFailed => _cloneFailed;

    /// <summary>Takes a new reference to the packet for the writer; a packet offered once the queue is closed is freed at once.</summary>
    public void Offer(AVPacket* packet, int stream)
    {
        var clone = ffmpeg.av_packet_clone(packet);
        if (clone is null)
        {
            _cloneFailed = true;
            Signal();
            return;
        }

        lock (_lock)
        {
            if (!_closed)
            {
                _queue.Enqueue(((nint)clone, stream));
                clone = null;
            }
        }

        if (clone is not null)
        {
            // Closed: the writer has finished.
            ffmpeg.av_packet_free(&clone);
            return;
        }

        Signal();
    }

    public bool TryTake(out (nint Packet, int Stream) item) => _queue.TryDequeue(out item);

    /// <summary>Waits for a signal, or the timeout.</summary>
    public void Wait(TimeSpan timeout) => _signal.Wait(timeout);

    /// <summary>Wakes the writer; never throws once it has finished.</summary>
    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // The writer has finished.
        }
    }

    /// <summary>Takes no more packets, and frees those still queued.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            while (_queue.TryDequeue(out var item))
            {
                HeldPacket.Free(item.Packet);
            }
        }
    }

    public void Dispose() => _signal.Dispose();
}
