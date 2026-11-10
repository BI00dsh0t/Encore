using System.Diagnostics;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Encore.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Encore.Platform.FFmpeg;

/// <summary>The session's encoder thread: emits exactly one frame per slot of the output fps. See docs/code-notes.md.</summary>
public sealed unsafe class PacedVideoEncoder : IDisposable
{
    /// <summary>The hand-off holds what the frame pool can spare beside NVENC's surfaces and the frames in use.</summary>
    public const int Capacity = 2;

    private const uint Infinite = 0xFFFFFFFF;
    private const long HundredNanosecondsPerSecond = 10_000_000;

    private readonly VideoEncoder _encoder;
    private readonly FramePacer _pacer;
    private readonly ILogger _logger;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly BoundedHandoff<PostedFrame> _queue = new(Capacity);
    private readonly Thread _thread;
    private HANDLE _wake;
    private HANDLE _timer;
    private AVFrame* _held;
    private long _heldQpc;
    private AVFrame* _last;
    private long _queueDrops;
    private volatile Exception? _fault;
    private bool _completed;

    public PacedVideoEncoder(VideoEncoder encoder, FramePacer pacer, ILogger logger)
    {
        _encoder = encoder;
        _pacer = pacer;
        _logger = logger;
        _wake = PInvoke.CreateEvent(null, false, false, default(PCWSTR));
        _timer = PInvoke.CreateWaitableTimerEx(
            null,
            default(PCWSTR),
            PInvoke.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            (uint)(SYNCHRONIZATION_ACCESS_RIGHTS.TIMER_MODIFY_STATE | SYNCHRONIZATION_ACCESS_RIGHTS.SYNCHRONIZATION_SYNCHRONIZE));
        if (_wake.IsNull || _timer.IsNull)
        {
            CloseHandles();
            throw new InvalidOperationException("Could not create the video encoder thread's event or high-resolution timer.");
        }

        _thread = new Thread(Run) { Name = "Video encoder", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Raised once, on the encoder thread, when an encode fails; later frames are released unencoded.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The pacer's counts; read them once <see cref="Complete"/> has returned.</summary>
    public FramePacer Pacer => _pacer;

    /// <summary>Frames not drawn because the hand-off was full.</summary>
    public long QueueDrops => Interlocked.Read(ref _queueDrops);

    public Exception? Fault => _fault;

    /// <summary>Called by the capture thread before it draws a frame: false when the hand-off is full; true means Post will fit.</summary>
    public bool TryReserve()
    {
        if (!_queue.IsFull)
        {
            return true;
        }

        Interlocked.Increment(ref _queueDrops);
        return false;
    }

    /// <summary>Hands a drawn frame and its capture QPC to the encoder thread; after Complete it is released unencoded.</summary>
    public void Post(EncoderFrame frame, long captureQpc)
    {
        if (!_queue.TryAdd(new PostedFrame(frame, captureQpc)))
        {
            VideoEncoder.Release(frame);
            Interlocked.Increment(ref _queueDrops);
            return;
        }

        PInvoke.SetEvent(_wake);
    }

    /// <summary>Offers what is queued, then joins the thread; call before <see cref="VideoEncoder.Flush"/>.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _queue.Complete();
        PInvoke.SetEvent(_wake);
        _thread.Join();
    }

    public void Dispose()
    {
        Complete();
        CloseHandles();
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                if (_queue.TryTakeNow(out var posted, out var drained))
                {
                    CatchUp(Stopwatch.GetTimestamp());
                    Offer(posted);
                    continue;
                }

                if (drained)
                {
                    break;
                }

                if (_last is null && !_pacer.HasHeld)
                {
                    // Nothing emitted yet: slot 0 is the anchor frame's, so wait for it, not the clock.
                    WaitForPost();
                    continue;
                }

                CatchUp(Stopwatch.GetTimestamp());
                WaitUntil(_pacer.NextDeadline);
            }
        }
        catch (Exception e)
        {
            RecordFault(e);
        }
        finally
        {
            while (_queue.TryTakeNow(out var left, out _))
            {
                VideoEncoder.Release(left.Frame);
            }

            VideoEncoder.Free(_held);
            _held = null;
            VideoEncoder.Free(_last);
            _last = null;
        }
    }

    /// <summary>Emits every slot that is due: the held frame at its slot's time, repeats half a slot after.</summary>
    private void CatchUp(long now)
    {
        if (_last is null && !_pacer.HasHeld)
        {
            return; // the anchor frame isn't offered yet: there is nothing to repeat
        }

        for (var step = _pacer.Poll(now); step.Action != PacerAction.None; step = _pacer.Poll(now))
        {
            if (step.Action == PacerAction.EmitHeld)
            {
                var held = _held;
                _held = null;
                Emit(held, step.Pts, _heldQpc);
            }
            else if (_last is not null && _fault is null)
            {
                Submit(VideoEncoder.Reference(_last), step.Pts, 0);
            }
        }
    }

    private void Offer(PostedFrame posted)
    {
        var decision = _pacer.Offer(posted.CaptureQpc);
        if (decision.ReplacedHeld)
        {
            VideoEncoder.Free(_held);
            _held = null;
        }

        switch (decision.Action)
        {
            case PacerAction.Emit:
                Emit(posted.Frame.Frame, decision.Pts, posted.CaptureQpc);
                break;
            case PacerAction.Hold:
                _held = posted.Frame.Frame;
                _heldQpc = posted.CaptureQpc;
                break;
            default:
                VideoEncoder.Release(posted.Frame);
                break;
        }
    }

    /// <summary>Encodes a new frame (consumed), keeping a reference to it for repeats.</summary>
    private void Emit(AVFrame* frame, long pts, long captureQpc)
    {
        if (_fault is not null)
        {
            VideoEncoder.Free(frame);
            return;
        }

        VideoEncoder.Free(_last);
        _last = null;
        _last = VideoEncoder.Reference(frame);
        Submit(frame, pts, captureQpc);
    }

    private void Submit(AVFrame* frame, long pts, long captureQpc)
    {
        if (_fault is not null)
        {
            VideoEncoder.Free(frame);
            return;
        }

        try
        {
            _encoder.SubmitSlot(frame, pts, captureQpc);
        }
        catch (Exception e)
        {
            RecordFault(e);
        }
    }

    private void RecordFault(Exception e)
    {
        if (_fault is not null)
        {
            return;
        }

        _logger.LogError(e, "Video encode failed on the encoder thread");
        _fault = e;
        Failed?.Invoke(e);
    }

    /// <summary>Sleeps until <paramref name="deadline"/> (QPC) or a posted frame, whichever is first.</summary>
    private void WaitUntil(long deadline)
    {
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0)
        {
            return;
        }

        // A negative due time is relative, in 100 ns.
        var due = -Math.Max(1, (long)((Int128)remaining * HundredNanosecondsPerSecond / _qpcFrequency));
        var handles = stackalloc HANDLE[2] { _wake, _timer };
        if (!PInvoke.SetWaitableTimer(_timer, &due, 0, null, null, false))
        {
            // Never sleep past the deadline: wait on the posted-frame event alone, in milliseconds.
            PInvoke.WaitForMultipleObjects(1, handles, false, (uint)Math.Max(1, remaining * 1000 / _qpcFrequency));
            return;
        }

        PInvoke.WaitForMultipleObjects(2, handles, false, Infinite);
    }

    private void WaitForPost()
    {
        var wake = _wake;
        PInvoke.WaitForMultipleObjects(1, &wake, false, Infinite);
    }

    private void CloseHandles()
    {
        if (!_timer.IsNull)
        {
            PInvoke.CloseHandle(_timer);
            _timer = default;
        }

        if (!_wake.IsNull)
        {
            PInvoke.CloseHandle(_wake);
            _wake = default;
        }
    }

    private readonly record struct PostedFrame(EncoderFrame Frame, long CaptureQpc);
}
