using System.Diagnostics;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Output;

namespace Encore.Platform.FFmpeg;

/// <summary>Writes one Recording from a tap's packets, on its own thread, as fragmented MP4. Rules: docs/code-notes.md.</summary>
public sealed unsafe class RecordingWriter
{
    /// <summary>How long a forced keyframe is waited for before the Recording starts on the next natural one.</summary>
    public static readonly TimeSpan KeyWait = TimeSpan.FromSeconds(1);

    /// <summary>How long after a stop the writer waits for every stream to pass it.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    /// <summary>The most held while waiting for the first keyframe, K, in µs of the newest packet's time.</summary>
    public const long HoldUs = 2_000_000;

    /// <summary>The longest fragment, in µs of video time: a kill loses at most about this much.</summary>
    public const long FragmentUs = 500_000;

    /// <summary>How far the video may run past a cut, in µs, before the cut is made without a late audio stream.</summary>
    public const long AudioWaitUs = 250_000;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private readonly string _path;
    private readonly RecordingStreams _streams;
    private readonly Func<long?> _forcedKey;
    private readonly long _pressUs;
    private readonly ILogger _log;
    private readonly Action<Exception?> _done;
    private readonly RecordingQueue _queue = new();
    private readonly List<HeldPacket> _held = [];
    private volatile bool _outputEnded;
    private long _stopUs = long.MaxValue;
    private long _stopQpc;
    private int _started;
    private long _keyDeadline;

    // Writer thread only.
    private int _videoStream = -1;
    private long? _targetUs;
    private long? _keyUs;
    private bool[] _passed = [];
    private RecordingFragments? _fragments;

    /// <summary>The writer owns the streams; forcedKey and done are called on the writer thread; pressUs is µs since the anchor.</summary>
    public RecordingWriter(
        string path, RecordingStreams streams, Func<long?> forcedKey, long pressUs, ILogger logger, Action<Exception?> done)
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

    /// <summary>Whether each fragment is flushed to the disk; false only for tests, which write hours of fragments.</summary>
    internal bool FlushToDisk { get; init; } = true;

    /// <summary>A test seam: the file fails as a full disk past this many bytes; null for none.</summary>
    internal long? CapacityBytes { get; init; }

    /// <summary>The packets offered and not yet taken by the writer thread; for tests to pace themselves.</summary>
    internal int Queued => _queue.Count;

    private long Written => _fragments?.Written ?? 0;

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

    /// <summary>Frees a writer that never started: detaches it and frees what it was handed; never calls back.</summary>
    public void Abandon()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        Tap?.Detach(this);
        _queue.Close();
        _streams.Dispose();
        _queue.Dispose();
    }

    /// <summary>Ends the Recording at a time in µs since the anchor; a second stop is ignored. Never blocks.</summary>
    public void Stop(long us)
    {
        Volatile.Write(ref _stopQpc, Stopwatch.GetTimestamp());
        Interlocked.CompareExchange(ref _stopUs, us, long.MaxValue);
        _queue.Signal();
    }

    /// <summary>Takes a new reference to the packet for the writer thread; called on the encoders' threads.</summary>
    internal void Offer(AVPacket* packet, int stream) => _queue.Offer(packet, stream);

    /// <summary>The session's output has ended: the writer finishes with what it was handed.</summary>
    internal void OutputEnded()
    {
        _outputEnded = true;
        _queue.Signal();
    }

    private void Run()
    {
        var background = BackgroundMode.Enter(_log);
        Exception? error = null;
        FragmentedMp4Muxer? muxer = null;
        long? keepBytes = null;
        try
        {
            muxer = Open();
            Loop(muxer);
            Finish(muxer);
        }
        catch (Exception e)
        {
            error = e;
            keepBytes = Salvage(muxer, e);
        }
        finally
        {
            muxer?.Dispose();
            _streams.Dispose();
            if (background)
            {
                BackgroundMode.Leave();
            }
        }

        if (keepBytes is { } keep)
        {
            RecordingFile.KeepWholeFragments(_path, keep, _log);
        }
        else if (error is not null && Written == 0)
        {
            OutputFiles.TryDelete(_path, _log);
        }

        _queue.Dispose();
        _done(error);
    }

    private FragmentedMp4Muxer Open()
    {
        var streams = _streams.Streams;
        _passed = new bool[streams.Count];
        var muxer = new FragmentedMp4Muxer(_path, _log, durable: true, FlushToDisk, CapacityBytes);
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

            _fragments = new RecordingFragments(_streams, _videoStream);
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
            _queue.Wait(Poll);

            // Read before the drain: every packet was queued before the output ended.
            var ended = _outputEnded;
            if (_targetUs is null)
            {
                ChooseTarget(muxer, ended, keyDeadline);
            }

            while (_queue.TryTake(out var item))
            {
                Take(muxer, item.Packet, item.Stream);
            }

            if (_queue.CloneFailed)
            {
                throw new InvalidOperationException("av_packet_clone failed; the Recording lost a packet.");
            }

            if (ended || StopReached())
            {
                return;
            }
        }
    }

    /// <summary>Sets K's target: the forced keyframe once it came, else the press once the wait is over; then looks in the hold.</summary>
    private void ChooseTarget(FragmentedMp4Muxer muxer, bool ended, long keyDeadline)
    {
        if (_forcedKey() is { } pts)
        {
            _targetUs = MicroTime.ToMicros(pts, _streams.Streams[_videoStream].TimeBase);
        }
        else if (ended || Stopwatch.GetTimestamp() >= keyDeadline)
        {
            _log.LogInformation(
                "No keyframe was forced after the press within {Seconds:0.0} s; the Recording starts on the next one",
                KeyWait.TotalSeconds);
            _targetUs = _pressUs;
        }

        if (_targetUs is not null)
        {
            FindKeyInHeld(muxer);
        }
    }

    /// <summary>A stop was asked and every stream has passed it, or the wait for them is over.</summary>
    private bool StopReached()
    {
        if (Interlocked.Read(ref _stopUs) == long.MaxValue)
        {
            return false;
        }

        var waited = Stopwatch.GetTimestamp() - Volatile.Read(ref _stopQpc) >= (long)(StopWait.TotalSeconds * Stopwatch.Frequency);
        return _passed.All(passed => passed) || waited;
    }

    /// <summary>The Recording ended normally: the rest is written, and the file is finished.</summary>
    private void Finish(FragmentedMp4Muxer muxer)
    {
        _fragments!.WritePending(muxer, long.MaxValue);
        Release();
        if (Written == 0)
        {
            throw new ErrorCodeException(ErrorCodes.SaveFailed, "The Recording ended before its first keyframe; nothing was written.");
        }

        muxer.Finish();
        _log.LogInformation(
            "Recording of {Packets} packets from {Start:0.000} s after the anchor written to {Path}", Written, _keyUs / 1e6, _path);
    }

    /// <summary>After a failure: frees what is held; returns the bytes a full disk keeps, else finishes what was written.</summary>
    private long? Salvage(FragmentedMp4Muxer? muxer, Exception error)
    {
        Release();
        if (WriteFailures.ReasonOf(error) == ErrorCodes.DiskFull)
        {
            // The trailer would fail too; the file is cut back to its last whole fragment.
            return muxer is { Fragments: > 0 } ? muxer.DurableBytes : 0;
        }

        if (muxer is not null && Written > 0)
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

        return null;
    }

    /// <summary>One packet from the queue: held until K is known, then written or freed.</summary>
    private void Take(FragmentedMp4Muxer muxer, nint packet, int stream)
    {
        var pointer = (AVPacket*)packet;
        var timeBase = _streams.Streams[stream].TimeBase;
        var timeUs = MicroTime.ToMicros(pointer->pts, timeBase);
        var video = stream == _videoStream;
        var atUs = video ? timeUs : timeUs + (MicroTime.ToMicros(pointer->duration, timeBase) / 2);
        if (atUs >= Interlocked.Read(ref _stopUs))
        {
            _passed[stream] = true;
        }

        if (_keyUs is not null)
        {
            Emit(muxer, packet, stream, timeUs, atUs);
            return;
        }

        var held = new HeldPacket(packet, stream, timeUs, atUs);
        _held.Add(held);
        if (_targetUs is { } target && video && held.IsKey && timeUs >= target)
        {
            StartAt(muxer, _held.Count - 1);
            return;
        }

        // At most HoldUs of packets wait for K.
        var drop = 0;
        while (drop < _held.Count && _held[drop].TimeUs < timeUs - HoldUs)
        {
            HeldPacket.Free(_held[drop].Packet);
            drop++;
        }

        _held.RemoveRange(0, drop);
    }

    private void FindKeyInHeld(FragmentedMp4Muxer muxer)
    {
        for (var i = 0; i < _held.Count; i++)
        {
            var held = _held[i];
            if (held.Stream == _videoStream && held.TimeUs >= _targetUs && held.IsKey)
            {
                StartAt(muxer, i);
                return;
            }
        }
    }

    /// <summary>K is the held video keyframe at this index: rebase every stream to it, then write what is held from it.</summary>
    private void StartAt(FragmentedMp4Muxer muxer, int key)
    {
        var keyPacket = _held[key];
        var keyUs = keyPacket.TimeUs;
        _keyUs = keyUs;
        _fragments!.Begin(keyUs, ((AVPacket*)keyPacket.Packet)->pts);
        _log.LogInformation(
            "The Recording starts on the keyframe {Seconds:0.000} s after the anchor ({Press:0.000} s after the press)",
            keyUs / 1e6,
            (keyUs - _pressUs) / 1e6);

        // Each packet leaves the hold as it is emitted, so a failed write leaves only the rest held, for Release to free.
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

    /// <summary>Keeps a packet from K and before the stop for the next cut, and frees any other; then makes every ready cut.</summary>
    private void Emit(FragmentedMp4Muxer muxer, nint packet, int stream, long timeUs, long atUs)
    {
        _fragments!.Reach(stream, atUs);

        // From K by its start, not its centre: with no edit list, an early audio packet would stretch the first frame.
        if (timeUs < _keyUs || atUs >= Interlocked.Read(ref _stopUs))
        {
            HeldPacket.Free(packet);
        }
        else
        {
            _fragments.Add(new HeldPacket(packet, stream, timeUs, atUs));
        }

        _fragments.Cut(muxer);
    }

    /// <summary>Detaches from the tap, then frees whatever is queued or held; a later Offer frees its own packet.</summary>
    private void Release()
    {
        Tap?.Detach(this);
        _queue.Close();
        foreach (var held in _held)
        {
            HeldPacket.Free(held.Packet);
        }

        _held.Clear();
        _fragments?.Release();
    }
}
