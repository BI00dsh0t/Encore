using Microsoft.Extensions.Logging;
using ReplayBuffer.Media;

namespace ReplayBuffer.Platform.FFmpeg;

/// <summary>
/// One dedicated thread that submits frames to a <see cref="VideoEncoder"/> and drains its
/// packets, so the capture thread only draws (story 1.2 experiment). Frames arrive through a
/// fixed-size, allocation-free hand-off; a frame that finds it full is not drawn at all and
/// is counted in <see cref="QueueDrops"/>.
/// </summary>
public sealed class EncoderThread : IDisposable
{
    /// <summary>
    /// The hand-off holds what the frame pool can spare: NVENC's surfaces, plus one frame being
    /// submitted and one being drawn, leave this many of <see cref="VideoEncoder.PoolSize"/>.
    /// </summary>
    public const int Capacity = VideoEncoder.PoolSize - VideoEncoder.NvencSurfaces - 2;

    private readonly VideoEncoder _encoder;
    private readonly ILogger _logger;
    private readonly BoundedHandoff<EncoderFrame> _queue = new(Capacity);
    private readonly Thread _thread;
    private long _queueDrops;
    private volatile Exception? _fault;
    private bool _completed;

    public EncoderThread(VideoEncoder encoder, ILogger logger)
    {
        _encoder = encoder;
        _logger = logger;
        _thread = new Thread(Run) { Name = "Video encoder", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Raised once, on the encoder thread, when a submit fails; later frames are released unencoded.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Frames not drawn because the hand-off was full.</summary>
    public long QueueDrops => Interlocked.Read(ref _queueDrops);

    public Exception? Fault => _fault;

    /// <summary>
    /// Called by the (single) capture thread before it draws a frame: false, counting a queue
    /// drop, when the hand-off is full. A true result guarantees the next <see cref="Post"/> fits.
    /// </summary>
    public bool TryReserve()
    {
        if (!_queue.IsFull)
        {
            return true;
        }

        Interlocked.Increment(ref _queueDrops);
        return false;
    }

    /// <summary>Hands a drawn frame to the encoder thread; after <see cref="Complete"/> it is released unencoded.</summary>
    public void Post(EncoderFrame frame)
    {
        if (!_queue.TryAdd(frame))
        {
            VideoEncoder.Release(frame);
            Interlocked.Increment(ref _queueDrops);
        }
    }

    /// <summary>Encodes what is queued, then joins the thread. Call before <see cref="VideoEncoder.Flush"/>.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _queue.Complete();
        _thread.Join();
    }

    public void Dispose() => Complete();

    private void Run()
    {
        while (_queue.TryTake(out var frame))
        {
            if (_fault is not null)
            {
                VideoEncoder.Release(frame);
                continue;
            }

            try
            {
                _encoder.Submit(frame);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Video encode failed on the encoder thread");
                _fault = e;
                Failed?.Invoke(e);
            }
        }
    }
}
