using System.Collections.Concurrent;
using System.Diagnostics;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace ReplayBuffer.Platform.FFmpeg;

/// <summary>
/// Writes one Recording (R2) from the packets a <see cref="RecordingTap"/> hands it, on a thread of
/// its own in background I/O mode (AD-7), into a <see cref="FragmentedMp4Muxer"/> at the path the
/// allocator reserved. It starts on the first video keyframe at or after the keyframe forced after
/// the press, or, when none is served within <see cref="KeyWait"/>, the first one at or after the
/// press. Each stream is rebased to that key K in its own time base, as <c>PacketRing.Cut</c> does:
/// the video from K, and every other packet whose centre is at or after K. Until K is known its
/// packets are held, at most <see cref="HoldUs"/> of them. A stop at E keeps the packets before E,
/// judged the same way, and the file is finished once every stream has passed E,
/// <see cref="StopWait"/> after the stop at the latest, or as soon as the session's output ends.
/// It cuts the fragments itself (R3, FR-12): one at each video keyframe and at least every
/// <see cref="FragmentUs"/>. A cut at video time V writes every packet before V (audio by its
/// centre) once every audio stream has reached V, or once the video is <see cref="AudioWaitUs"/>
/// past V, so no Track's fragment runs ahead of the video; then the fragment goes to the disk.
/// Every packet it was handed is freed on every path. It owns its streams' parameters, so the
/// session can free the tap, the ring and the encoders while it still finishes.
/// </summary>
public sealed unsafe class RecordingWriter
{
    /// <summary>How long a forced keyframe is waited for before the Recording starts on the next natural one.</summary>
    public static readonly TimeSpan KeyWait = TimeSpan.FromSeconds(1);

    /// <summary>How long after a stop the writer waits for every stream to pass it.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    /// <summary>The most held before K is known, in µs of the newest packet's time.</summary>
    public const long HoldUs = 2_000_000;

    /// <summary>The longest fragment, in µs of video time: a kill loses at most about this much.</summary>
    public const long FragmentUs = 500_000;

    /// <summary>How far the video may run past a cut, in µs, before it is made without an audio stream that hasn't reached it.</summary>
    public const long AudioWaitUs = 250_000;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private static readonly Comparison<Held> ByTime = (a, b) => a.TimeUs != b.TimeUs ? a.TimeUs.CompareTo(b.TimeUs) : a.Stream.CompareTo(b.Stream);

    private readonly string _path;
    private readonly RecordingStreams _streams;
    private readonly Func<long?> _forcedKey;
    private readonly long _pressUs;
    private readonly ILogger _log;
    private readonly Action<Exception?> _done;
    private readonly ConcurrentQueue<(nint Packet, int Stream)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Lock _queueLock = new();
    private readonly List<Held> _held = [];
    private readonly List<Held> _pending = [];
    private readonly List<Held> _batch = [];
    private bool _closed;
    private volatile bool _outputEnded;
    private volatile bool _cloneFailed;
    private long _stopUs = long.MaxValue;
    private long _stopQpc;
    private int _started;
    private long _keyDeadline;

    // Writer thread only.
    private int _videoStream = -1;
    private long? _targetUs;
    private long? _keyUs;
    private long[] _origins = [];
    private bool[] _passed = [];
    private long[] _reached = [];
    private long _fragmentUs;
    private long? _cutUs;
    private long _written;

    /// <param name="path">The reserved file, written in place.</param>
    /// <param name="streams">The Recording's streams; the writer owns them from here.</param>
    /// <param name="forcedKey">The forced keyframe's PTS in the video stream's time base once it is served, else null; called on the writer thread.</param>
    /// <param name="pressUs">The press, in µs since the session's anchor.</param>
    /// <param name="logger">The writer's log.</param>
    /// <param name="done">Called once, on the writer thread, with null or the failure, after the file is finished or removed.</param>
    public RecordingWriter(string path, RecordingStreams streams, Func<long?> forcedKey, long pressUs, ILogger logger, Action<Exception?> done)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(forcedKey);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(done);
        _path = path;
        _streams = streams;
        _forcedKey = forcedKey;
        _pressUs = pressUs;
        _log = logger;
        _done = done;
    }

    /// <summary>The tap it is attached to, set by <see cref="RecordingTap.Attach"/>.</summary>
    internal RecordingTap? Tap { get; set; }

    /// <summary>Whether each fragment is flushed to the disk (R3); false only for tests, which write hours of fragments.</summary>
    internal bool FlushToDisk { get; init; } = true;

    /// <summary>The packets offered and not yet taken by the writer thread; for tests to pace themselves.</summary>
    internal int Queued => _queue.Count;

    /// <summary>Starts the writer thread, once.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The Recording has already started.");
        }

        // The forced key's wait runs from the press's handling, not from the file's opening.
        _keyDeadline = Stopwatch.GetTimestamp() + (long)(KeyWait.TotalSeconds * Stopwatch.Frequency);
        try
        {
            new Thread(Run) { Name = "RecordingWriter", IsBackground = true }.Start();
        }
        catch
        {
            // Never started: Abandon frees it.
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    /// <summary>Frees a writer that never started: detaches it, frees what it was handed and its streams; never calls back.</summary>
    public void Abandon()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        Tap?.Detach(this);
        CloseQueue();
        _streams.Dispose();
        _signal.Dispose();
    }

    /// <summary>Ends the Recording at <paramref name="us"/> (µs since the anchor); a second stop is ignored. Never blocks.</summary>
    public void Stop(long us)
    {
        Volatile.Write(ref _stopQpc, Stopwatch.GetTimestamp());
        Interlocked.CompareExchange(ref _stopUs, us, long.MaxValue);
        Signal();
    }

    /// <summary>Takes a new reference to <paramref name="packet"/> for the writer thread; called on the encoders' threads.</summary>
    internal void Offer(AVPacket* packet, int stream)
    {
        var clone = ffmpeg.av_packet_clone(packet);
        if (clone is null)
        {
            _cloneFailed = true;
            Signal();
            return;
        }

        lock (_queueLock)
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

    /// <summary>The session's output has ended: the writer finishes with what it was handed.</summary>
    internal void OutputEnded()
    {
        _outputEnded = true;
        Signal();
    }

    private static long ToMicros(long ticks, AVRational timeBase) =>
        (long)((Int128)ticks * 1_000_000 * timeBase.num / timeBase.den);

    private static long FromMicros(long us, AVRational timeBase) =>
        (long)((Int128)us * timeBase.den / ((Int128)1_000_000 * timeBase.num));

    private static void Free(nint packet)
    {
        var pointer = (AVPacket*)packet;
        ffmpeg.av_packet_free(&pointer);
    }

    private void Signal()
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

    private void Run()
    {
        var background = ClipWriter.EnterBackgroundMode(_log);
        Exception? error = null;
        FragmentedMp4Muxer? muxer = null;
        try
        {
            muxer = Open();
            Loop(muxer);
            WritePending(muxer, long.MaxValue);
            Release();
            if (_written == 0)
            {
                throw new ErrorCodeException(ErrorCodes.SaveFailed, "The Recording ended before its first keyframe; nothing was written.");
            }

            muxer.Finish();
            _log.LogInformation("Recording of {Packets} packets from {Start:0.000} s after the anchor written to {Path}", _written, _keyUs / 1e6, _path);
        }
        catch (Exception e)
        {
            error = e;
            Release();
            if (muxer is not null && _written > 0)
            {
                // What it holds stays playable: finish it as far as it got.
                try
                {
                    muxer.Finish();
                }
                catch (Exception finish)
                {
                    _log.LogWarning("Could not finish the failed Recording {Path}: {Message}", _path, finish.Message);
                }
            }
        }
        finally
        {
            muxer?.Dispose();
            _streams.Dispose();
            if (background)
            {
                PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(), THREAD_PRIORITY.THREAD_MODE_BACKGROUND_END);
            }
        }

        if (error is not null && _written == 0)
        {
            TryDelete();
        }

        _signal.Dispose();
        _done(error);
    }

    private FragmentedMp4Muxer Open()
    {
        var streams = _streams.Streams;
        _origins = new long[streams.Count];
        _passed = new bool[streams.Count];
        _reached = new long[streams.Count];
        Array.Fill(_reached, long.MinValue);
        var muxer = new FragmentedMp4Muxer(_path, _log, durable: true, FlushToDisk);
        try
        {
            for (var i = 0; i < streams.Count; i++)
            {
                var stream = streams[i];
                if (_videoStream < 0 && stream.Parameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    _videoStream = i;
                }

                muxer.AddStream(stream.Parameters, stream.TimeBase, stream.Title, stream.IsDefault);
            }

            if (_videoStream < 0)
            {
                throw new InvalidOperationException("The session has no video stream to record.");
            }

            muxer.Start();
            return muxer;
        }
        catch
        {
            muxer.Dispose();
            throw;
        }
    }

    private void Loop(FragmentedMp4Muxer muxer)
    {
        var keyDeadline = _keyDeadline;
        while (true)
        {
            _signal.Wait(Poll);

            // Read before the drain: every packet was queued before the output ended.
            var ended = _outputEnded;
            if (_targetUs is null)
            {
                if (_forcedKey() is { } pts)
                {
                    _targetUs = ToMicros(pts, _streams.Streams[_videoStream].TimeBase);
                }
                else if (ended || Stopwatch.GetTimestamp() >= keyDeadline)
                {
                    _log.LogInformation("No keyframe was forced after the press within {Seconds:0.0} s; the Recording starts on the next one", KeyWait.TotalSeconds);
                    _targetUs = _pressUs;
                }

                if (_targetUs is not null)
                {
                    FindKeyInHeld(muxer);
                }
            }

            while (_queue.TryDequeue(out var item))
            {
                Take(muxer, item.Packet, item.Stream);
            }

            if (_cloneFailed)
            {
                throw new InvalidOperationException("av_packet_clone failed; the Recording lost a packet.");
            }

            if (ended)
            {
                return;
            }

            var stopUs = Interlocked.Read(ref _stopUs);
            if (stopUs != long.MaxValue
                && (_passed.All(p => p) || Stopwatch.GetTimestamp() - Volatile.Read(ref _stopQpc) >= (long)(StopWait.TotalSeconds * Stopwatch.Frequency)))
            {
                return;
            }
        }
    }

    /// <summary>One packet from the queue: held until K is known, then written or freed.</summary>
    private void Take(FragmentedMp4Muxer muxer, nint packet, int stream)
    {
        var pointer = (AVPacket*)packet;
        var timeBase = _streams.Streams[stream].TimeBase;
        var timeUs = ToMicros(pointer->pts, timeBase);
        var video = stream == _videoStream;
        var atUs = video ? timeUs : timeUs + (ToMicros(pointer->duration, timeBase) / 2);
        if (atUs >= Interlocked.Read(ref _stopUs))
        {
            _passed[stream] = true;
        }

        if (_keyUs is null)
        {
            _held.Add(new Held(packet, stream, timeUs, atUs));
            if (_targetUs is { } target && video && (pointer->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0 && timeUs >= target)
            {
                StartAt(muxer, _held.Count - 1);
                return;
            }

            // At most HoldUs of packets wait for K.
            var drop = 0;
            while (drop < _held.Count && _held[drop].TimeUs < timeUs - HoldUs)
            {
                Free(_held[drop].Packet);
                drop++;
            }

            _held.RemoveRange(0, drop);
            return;
        }

        Emit(muxer, packet, stream, timeUs, atUs);
    }

    private void FindKeyInHeld(FragmentedMp4Muxer muxer)
    {
        for (var i = 0; i < _held.Count; i++)
        {
            var held = _held[i];
            if (held.Stream == _videoStream && held.TimeUs >= _targetUs && (((AVPacket*)held.Packet)->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0)
            {
                StartAt(muxer, i);
                return;
            }
        }
    }

    /// <summary>K is the held video keyframe at <paramref name="key"/>: rebase every stream to it, then write what is held from it.</summary>
    private void StartAt(FragmentedMp4Muxer muxer, int key)
    {
        var keyPacket = _held[key];
        var keyUs = keyPacket.TimeUs;
        _keyUs = keyUs;
        _fragmentUs = keyUs;
        var streams = _streams.Streams;
        for (var s = 0; s < streams.Count; s++)
        {
            _origins[s] = s == _videoStream ? ((AVPacket*)keyPacket.Packet)->pts : FromMicros(keyUs, streams[s].TimeBase);
        }

        _log.LogInformation("The Recording starts on the keyframe {Seconds:0.000} s after the anchor ({Press:0.000} s after the press)", keyUs / 1e6, (keyUs - _pressUs) / 1e6);
        // Each packet leaves the hold as it is emitted (Emit frees it even on a throw), so a failed
        // write leaves only the rest held, for Release to free.
        var emitted = 0;
        try
        {
            while (emitted < _held.Count)
            {
                var held = _held[emitted++];
                Emit(muxer, held.Packet, held.Stream, held.TimeUs, held.AtUs);
            }
        }
        finally
        {
            _held.RemoveRange(0, emitted);
        }
    }

    /// <summary>
    /// Keeps a packet from K and before the stop for the next cut, and frees any other; takes the
    /// packet either way. Then makes every cut that is ready.
    /// </summary>
    private void Emit(FragmentedMp4Muxer muxer, nint packet, int stream, long timeUs, long atUs)
    {
        _reached[stream] = Math.Max(_reached[stream], atUs);
        if (atUs < _keyUs || atUs >= Interlocked.Read(ref _stopUs))
        {
            Free(packet);
        }
        else
        {
            _pending.Add(new Held(packet, stream, timeUs, atUs));
            if (_cutUs is null && stream == _videoStream && IsCut(packet, atUs))
            {
                _cutUs = atUs;
            }
        }

        Cut(muxer);
    }

    /// <summary>A video packet at <paramref name="atUs"/> starts the next fragment: a keyframe, or <see cref="FragmentUs"/> into this one.</summary>
    private bool IsCut(nint packet, long atUs) =>
        atUs > _fragmentUs && (atUs >= _fragmentUs + FragmentUs || (((AVPacket*)packet)->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0);

    /// <summary>Makes each cut that is ready: the packets before it go out as one fragment, flushed to the disk.</summary>
    private void Cut(FragmentedMp4Muxer muxer)
    {
        while (_cutUs is { } cut && Ready(cut))
        {
            WritePending(muxer, cut);
            muxer.CutFragment();
            _fragmentUs = cut;
            _cutUs = null;
            foreach (var pending in _pending)
            {
                if (pending.Stream == _videoStream && IsCut(pending.Packet, pending.AtUs))
                {
                    _cutUs = pending.AtUs;
                    break;
                }
            }
        }
    }

    /// <summary>Every audio stream has reached <paramref name="cut"/>, or the video is <see cref="AudioWaitUs"/> past it.</summary>
    private bool Ready(long cut)
    {
        if (_reached[_videoStream] >= cut + AudioWaitUs)
        {
            return true;
        }

        for (var s = 0; s < _reached.Length; s++)
        {
            if (s != _videoStream && _reached[s] < cut)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes the pending packets before <paramref name="beforeUs"/> in time order across the streams,
    /// as an interleaver would, rebased; the rest stay pending. Each one is freed on every path.
    /// </summary>
    private void WritePending(FragmentedMp4Muxer muxer, long beforeUs)
    {
        var kept = 0;
        for (var i = 0; i < _pending.Count; i++)
        {
            var pending = _pending[i];
            if (pending.AtUs < beforeUs)
            {
                _batch.Add(pending);
            }
            else
            {
                _pending[kept++] = pending;
            }
        }

        _pending.RemoveRange(kept, _pending.Count - kept);

        // Each stream's times only grow, so the sort keeps every stream's own order; the earliest
        // packet goes first, so the muxer's shift for negative times (AAC's first frame) holds for all.
        _batch.Sort(ByTime);
        var next = 0;
        try
        {
            while (next < _batch.Count)
            {
                var item = _batch[next++];
                var pointer = (AVPacket*)item.Packet;
                try
                {
                    var origin = _origins[item.Stream];
                    pointer->pts -= origin;
                    if (pointer->dts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        pointer->dts -= origin;
                    }

                    // Takes the packet's reference whether it succeeds or not; the struct is freed below.
                    muxer.Write(pointer, item.Stream, _streams.Streams[item.Stream].TimeBase);
                    _written++;
                }
                finally
                {
                    ffmpeg.av_packet_free(&pointer);
                }
            }
        }
        finally
        {
            // On a throw, the rest of the batch is freed unwritten.
            while (next < _batch.Count)
            {
                Free(_batch[next++].Packet);
            }

            _batch.Clear();
        }
    }

    /// <summary>Detaches from the tap, then frees whatever is queued or held; an Offer after this frees its own.</summary>
    private void Release()
    {
        Tap?.Detach(this);
        CloseQueue();
        foreach (var held in _held)
        {
            Free(held.Packet);
        }

        _held.Clear();
        foreach (var pending in _pending)
        {
            Free(pending.Packet);
        }

        _pending.Clear();
    }

    private void CloseQueue()
    {
        lock (_queueLock)
        {
            _closed = true;
            while (_queue.TryDequeue(out var item))
            {
                Free(item.Packet);
            }
        }
    }

    private void TryDelete()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not delete {Path}: {Message}", _path, e.Message);
        }
    }

    private readonly record struct Held(nint Packet, int Stream, long TimeUs, long AtUs);
}
