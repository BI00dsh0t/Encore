using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>A Recording's packets from its first keyframe on: held for the next cut, then written a fragment at a time.</summary>
internal sealed unsafe class RecordingFragments
{
    private static readonly Comparison<HeldPacket> ByTime =
        (first, second) => first.TimeUs != second.TimeUs ? first.TimeUs.CompareTo(second.TimeUs) : first.Stream.CompareTo(second.Stream);

    private readonly RecordingStreams _streams;
    private readonly int _videoStream;
    private readonly List<HeldPacket> _pending = [];
    private readonly List<HeldPacket> _batch = [];
    private readonly long[] _origins;
    private readonly long[] _reached;
    private long _fragmentUs;
    private long? _cutUs;

    public RecordingFragments(RecordingStreams streams, int videoStream)
    {
        _streams = streams;
        _videoStream = videoStream;
        _origins = new long[streams.Streams.Count];
        _reached = new long[streams.Streams.Count];
        Array.Fill(_reached, long.MinValue);
    }

    /// <summary>Packets written to the muxer.</summary>
    public long Written { get; private set; }

    /// <summary>Rebases every stream to the key: the video to its PTS, the others to its time in their own time base.</summary>
    public void Begin(long keyUs, long keyPts)
    {
        _fragmentUs = keyUs;
        var all = _streams.Streams;
        for (var stream = 0; stream < all.Count; stream++)
        {
            _origins[stream] = stream == _videoStream ? keyPts : MicroTime.FromMicros(keyUs, all[stream].TimeBase);
        }
    }

    /// <summary>Notes how far a stream has reached, by a packet's centre.</summary>
    public void Reach(int stream, long atUs) => _reached[stream] = Math.Max(_reached[stream], atUs);

    /// <summary>Keeps a packet for the next cut; the first video packet that can start a fragment marks the cut.</summary>
    public void Add(HeldPacket packet)
    {
        _pending.Add(packet);
        if (_cutUs is null && packet.Stream == _videoStream && IsCut(packet))
        {
            _cutUs = packet.AtUs;
        }
    }

    /// <summary>Makes each cut that is ready: the packets before it go out as one fragment, flushed to the disk.</summary>
    public void Cut(FragmentedMp4Muxer muxer)
    {
        while (_cutUs is { } cut && Ready(cut))
        {
            WritePending(muxer, cut);
            muxer.CutFragment();
            _fragmentUs = cut;
            _cutUs = null;
            foreach (var pending in _pending)
            {
                if (pending.Stream == _videoStream && IsCut(pending))
                {
                    _cutUs = pending.AtUs;
                    break;
                }
            }
        }
    }

    /// <summary>Writes the pending packets before a time, in time order across the streams; each is freed on every path.</summary>
    public void WritePending(FragmentedMp4Muxer muxer, long beforeUs)
    {
        TakeBatch(beforeUs);

        // Each stream's times only grow, so sorting by time keeps every stream's own order.
        _batch.Sort(ByTime);
        var next = 0;
        try
        {
            while (next < _batch.Count)
            {
                WriteOne(muxer, _batch[next++]);
            }
        }
        finally
        {
            // On a throw, the rest of the batch is freed unwritten.
            while (next < _batch.Count)
            {
                HeldPacket.Free(_batch[next++].Packet);
            }

            _batch.Clear();
        }
    }

    /// <summary>Frees every pending packet.</summary>
    public void Release()
    {
        foreach (var pending in _pending)
        {
            HeldPacket.Free(pending.Packet);
        }

        _pending.Clear();
    }

    /// <summary>A video packet that starts the next fragment: a keyframe, or one fragment's length into this one.</summary>
    private bool IsCut(HeldPacket packet) =>
        packet.AtUs > _fragmentUs && (packet.AtUs >= _fragmentUs + RecordingWriter.FragmentUs || packet.IsKey);

    /// <summary>Every audio stream has reached the cut, or the video is the audio wait past it.</summary>
    private bool Ready(long cut)
    {
        if (_reached[_videoStream] >= cut + RecordingWriter.AudioWaitUs)
        {
            return true;
        }

        for (var stream = 0; stream < _reached.Length; stream++)
        {
            if (stream != _videoStream && _reached[stream] < cut)
            {
                return false;
            }
        }

        return true;
    }

    // Moves the pending packets before a time into the batch, keeping the rest in order.
    private void TakeBatch(long beforeUs)
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
    }

    private void WriteOne(FragmentedMp4Muxer muxer, HeldPacket item)
    {
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
            Written++;
        }
        finally
        {
            ffmpeg.av_packet_free(&pointer);
        }
    }
}
