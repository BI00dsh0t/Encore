using ReplayBuffer.Core.Audio;
using ReplayBuffer.Media;

namespace ReplayBuffer.Engine.Audio;

/// <summary>
/// A tap on one hub device, for a capture session or a meter watch. It subscribes before the anchor
/// and drops blocks until <see cref="Publish"/> hands it its <see cref="AudioTimeline"/>; from then
/// on each block goes to the timeline, which cuts the stream at the anchor and, since hub blocks are
/// contiguous and QPC-locked, writes the rest as-is. Called on the device's thread.
/// </summary>
internal class AudioTimelineTap : IAudioPcmSubscriber
{
    private volatile AudioTimeline? _timeline;

    public AudioTimeline? Timeline => _timeline;

    public void Publish(AudioTimeline timeline) => _timeline = timeline;

    public void OnPcm(in AudioBlock block)
    {
        var timeline = _timeline;
        timeline?.Push(block.StartQpc, block.Frames, block.Interleaved, block.IsSilent ? AudioPacketFlags.Silent : AudioPacketFlags.None);
    }
}
