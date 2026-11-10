using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Encore.Media;

namespace Encore.Platform.FFmpeg;

/// <summary>
/// AAC-LC at 48 kHz stereo (AD-9), fed by an <see cref="AudioTimeline"/>. Samples go
/// through an audio FIFO into 1024-sample frames; PTS is the sample count since the anchor.
/// </summary>
public sealed unsafe class AacEncoder : IAudioSampleSink, IDisposable
{
    private const int BitRate = 192_000;
    private const int ChunkFrames = 4096;

    private readonly PacketSink _muxer;
    private readonly Lock _lock = new();
    private readonly int _streamIndex;
    private readonly float[] _left = new float[ChunkFrames];
    private readonly float[] _right = new float[ChunkFrames];
    private AVCodecContext* _context;
    private AVAudioFifo* _fifo;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private long _startSample;
    private long _samplesSent;

    /// <param name="muxer">Where packets go: a file or Rewind.</param>
    /// <param name="startSample">
    /// The PTS of its first sample, in samples since the session anchor: 0 for a stream that starts
    /// at the anchor; later for an encoder chain that starts mid-session (4.9), whose priming
    /// packets, ending at or before it, are dropped here as a sink drops them at 0.
    /// </param>
    public AacEncoder(PacketSink muxer, string title, bool isDefault, ILogger logger, long startSample = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSample);
        _muxer = muxer;
        _startSample = startSample;
        _samplesSent = startSample;

        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec is null)
        {
            throw new InvalidOperationException("This FFmpeg build has no AAC encoder.");
        }

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _context->sample_rate = AudioTimeline.SampleRate;
        ffmpeg.av_channel_layout_default(&_context->ch_layout, AudioTimeline.Channels);
        _context->bit_rate = BitRate;
        _context->profile = ffmpeg.AV_PROFILE_AAC_LOW;
        _context->time_base = new AVRational { num = 1, den = AudioTimeline.SampleRate };
        if (muxer.NeedsGlobalHeader)
        {
            _context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }

        FFmpegLibrary.Check(ffmpeg.avcodec_open2(_context, codec, null), "avcodec_open2(aac)");

        _fifo = ffmpeg.av_audio_fifo_alloc(AVSampleFormat.AV_SAMPLE_FMT_FLTP, AudioTimeline.Channels, AudioTimeline.SampleRate);
        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _frame->sample_rate = AudioTimeline.SampleRate;
        _frame->nb_samples = _context->frame_size;
        FFmpegLibrary.Check(ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &_context->ch_layout), "av_channel_layout_copy");
        FFmpegLibrary.Check(ffmpeg.av_frame_get_buffer(_frame, 0), "av_frame_get_buffer(audio)");
        _packet = ffmpeg.av_packet_alloc();

        _streamIndex = muxer.AddStream(_context, title, isDefault);
        logger.LogInformation("AAC-LC stream '{Title}'{Default}", title, isDefault ? " (default)" : "");
    }

    /// <summary>Frames written into the encoder so far, silence included.</summary>
    public long SamplesWritten { get; private set; }

    /// <summary>
    /// Sets the first sample's PTS, as the constructor's <c>startSample</c> does, for an encoder
    /// opened before its start is known (a chain built in advance, 4.9). Only before the first write.
    /// </summary>
    /// <exception cref="InvalidOperationException">A sample has already been written.</exception>
    public void StartAt(long startSample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSample);
        lock (_lock)
        {
            if (SamplesWritten != 0)
            {
                throw new InvalidOperationException("The AAC stream has already started.");
            }

            _startSample = startSample;
            _samplesSent = startSample;
        }
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        lock (_lock)
        {
            while (interleaved.Length > 0)
            {
                var frames = Math.Min(ChunkFrames, interleaved.Length / AudioTimeline.Channels);
                for (var i = 0; i < frames; i++)
                {
                    _left[i] = interleaved[2 * i];
                    _right[i] = interleaved[(2 * i) + 1];
                }

                WritePlanar(_left, _right, frames);
                interleaved = interleaved[(frames * AudioTimeline.Channels)..];
            }

            Drain(final: false);
        }
    }

    public void WriteSilence(long frames)
    {
        lock (_lock)
        {
            Array.Clear(_left);
            Array.Clear(_right);
            while (frames > 0)
            {
                var chunk = (int)Math.Min(ChunkFrames, frames);
                WritePlanar(_left, _right, chunk);
                frames -= chunk;
                Drain(final: false);
            }
        }
    }

    /// <summary>Encodes what is left in the FIFO, drains the encoder and writes its packets.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_context is null)
            {
                return;
            }

            Drain(final: true);
            FFmpegLibrary.Check(ffmpeg.avcodec_send_frame(_context, null), "avcodec_send_frame(aac flush)");
            ReceivePackets();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_packet is not null)
            {
                fixed (AVPacket** packet = &_packet)
                {
                    ffmpeg.av_packet_free(packet);
                }
            }

            if (_frame is not null)
            {
                fixed (AVFrame** frame = &_frame)
                {
                    ffmpeg.av_frame_free(frame);
                }
            }

            if (_fifo is not null)
            {
                ffmpeg.av_audio_fifo_free(_fifo);
                _fifo = null;
            }

            if (_context is not null)
            {
                fixed (AVCodecContext** context = &_context)
                {
                    ffmpeg.avcodec_free_context(context);
                }
            }
        }
    }

    private void WritePlanar(float[] left, float[] right, int frames)
    {
        fixed (float* l = left)
        fixed (float* r = right)
        {
            var planes = stackalloc void*[2] { l, r };
            if (ffmpeg.av_audio_fifo_write(_fifo, planes, frames) < frames)
            {
                throw new InvalidOperationException("av_audio_fifo_write failed.");
            }
        }

        SamplesWritten += frames;
    }

    private void Drain(bool final)
    {
        var frameSize = _context->frame_size;
        while (true)
        {
            var available = ffmpeg.av_audio_fifo_size(_fifo);
            if (available == 0 || (!final && available < frameSize))
            {
                return;
            }

            var count = Math.Min(available, frameSize);
            FFmpegLibrary.Check(ffmpeg.av_frame_make_writable(_frame), "av_frame_make_writable(audio)");
            _frame->nb_samples = count;
            if (ffmpeg.av_audio_fifo_read(_fifo, (void**)&_frame->data, count) < count)
            {
                throw new InvalidOperationException("av_audio_fifo_read failed.");
            }

            _frame->pts = _samplesSent;
            _samplesSent += count;
            FFmpegLibrary.Check(ffmpeg.avcodec_send_frame(_context, _frame), "avcodec_send_frame(aac)");
            ReceivePackets();
        }
    }

    private void ReceivePackets()
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (result == FFmpegLibrary.ErrorAgain || result == FFmpegLibrary.ErrorEof)
            {
                return;
            }

            FFmpegLibrary.Check(result, "avcodec_receive_packet(aac)");
            if (_startSample > 0 && _packet->pts != ffmpeg.AV_NOPTS_VALUE && _packet->pts + _packet->duration <= _startSample)
            {
                // The priming before a mid-session start: played, it would delay the stream.
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            _muxer.Write(_packet, _streamIndex, _context->time_base);
        }
    }
}
