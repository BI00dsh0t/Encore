using Encore.Core.Audio;
using Encore.Media;

namespace Encore.Engine.Audio;

/// <summary>A tap on one hub device; it can hold the newest blocks until Publish, so a stream starts with real audio.</summary>
internal class AudioTimelineTap : IAudioPcmSubscriber
{
    /// <summary>The most blocks held at once; the oldest is dropped beyond it.</summary>
    private const int MaxHeldBlocks = 1024;

    private readonly Lock _lock = new();
    private readonly int _holdFrames;

    // Held samples, in block order, in [_sampleStart, _sampleEnd); twice the hold, so compacting is rare.
    private readonly float[] _samples;
    private readonly HeldBlock[] _blocks;
    private int _firstBlock;
    private int _blockCount;
    private long _heldFrames;
    private int _sampleStart;
    private int _sampleEnd;
    private volatile AudioTimeline? _timeline;

    /// <param name="holdFrames">How many frames of blocks to keep before <see cref="Publish"/>; 0 drops them.</param>
    public AudioTimelineTap(int holdFrames = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(holdFrames);
        _holdFrames = holdFrames;
        _samples = holdFrames == 0 ? [] : new float[2 * holdFrames * AudioFormat.Channels];
        _blocks = holdFrames == 0 ? [] : new HeldBlock[MaxHeldBlocks];
    }

    public void Publish(AudioTimeline timeline)
    {
        if (_holdFrames == 0)
        {
            _timeline = timeline;
            return;
        }

        lock (_lock)
        {
            for (var i = 0; i < _blockCount; i++)
            {
                var block = _blocks[(_firstBlock + i) % _blocks.Length];
                if (block.Silent)
                {
                    timeline.Push(block.StartQpc, block.Frames, default, AudioPacketFlags.Silent);
                }
                else
                {
                    var samples = _samples.AsSpan(block.Offset, block.Frames * AudioFormat.Channels);
                    timeline.Push(block.StartQpc, block.Frames, samples, AudioPacketFlags.None);
                }
            }

            _firstBlock = 0;
            _blockCount = 0;
            _heldFrames = 0;
            _sampleStart = 0;
            _sampleEnd = 0;
            _timeline = timeline;
        }
    }

    public void OnPcm(in AudioBlock block)
    {
        if (_holdFrames == 0)
        {
            Push(_timeline, block);
            return;
        }

        lock (_lock)
        {
            if (_timeline is { } timeline)
            {
                Push(timeline, block);
            }
            else
            {
                Hold(block);
            }
        }
    }

    private static void Push(AudioTimeline? timeline, in AudioBlock block) =>
        timeline?.Push(block.StartQpc, block.Frames, block.Interleaved, block.IsSilent ? AudioPacketFlags.Silent : AudioPacketFlags.None);

    /// <summary>Keeps <paramref name="block"/>, dropping the oldest held blocks to make room. Under the lock.</summary>
    private void Hold(in AudioBlock block)
    {
        var frames = block.Frames;
        if (frames <= 0)
        {
            return;
        }

        if (frames > _holdFrames)
        {
            // Larger than the whole hold: nothing older can stay beside it, and it can't be kept either.
            while (_blockCount > 0)
            {
                DropOldest();
            }

            return;
        }

        while (_blockCount > 0 && (_heldFrames + frames > _holdFrames || _blockCount == _blocks.Length))
        {
            DropOldest();
        }

        var offset = 0;
        if (!block.IsSilent)
        {
            var length = frames * AudioFormat.Channels;
            if (_sampleEnd + length > _samples.Length)
            {
                Compact();
            }

            // Held frames stay within the hold, half the buffer, so after compacting it fits.
            offset = _sampleEnd;
            block.Interleaved[..length].CopyTo(_samples.AsSpan(offset));
            _sampleEnd += length;
        }

        _blocks[(_firstBlock + _blockCount) % _blocks.Length] = new HeldBlock(block.StartQpc, frames, offset, block.IsSilent);
        _blockCount++;
        _heldFrames += frames;
    }

    private void DropOldest()
    {
        var oldest = _blocks[_firstBlock];
        _firstBlock = (_firstBlock + 1) % _blocks.Length;
        _blockCount--;
        _heldFrames -= oldest.Frames;
        if (_blockCount == 0)
        {
            _sampleStart = 0;
            _sampleEnd = 0;
        }
        else if (!oldest.Silent)
        {
            _sampleStart = oldest.Offset + (oldest.Frames * AudioFormat.Channels);
        }
    }

    /// <summary>Moves the held samples to the front of the buffer.</summary>
    private void Compact()
    {
        if (_sampleStart == 0)
        {
            return;
        }

        _samples.AsSpan(_sampleStart, _sampleEnd - _sampleStart).CopyTo(_samples);
        for (var i = 0; i < _blockCount; i++)
        {
            ref var held = ref _blocks[(_firstBlock + i) % _blocks.Length];
            if (!held.Silent)
            {
                held = held with { Offset = held.Offset - _sampleStart };
            }
        }

        _sampleEnd -= _sampleStart;
        _sampleStart = 0;
    }

    private readonly record struct HeldBlock(long StartQpc, int Frames, int Offset, bool Silent);
}
