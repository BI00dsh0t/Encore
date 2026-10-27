using ReplayBuffer.Core.Audio;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Media;

namespace ReplayBuffer.Engine.Sessions;

/// <summary>
/// A session's tap on one hub device. It subscribes when the session opens, before the anchor, and
/// drops blocks until <see cref="Publish"/> hands it the session's <see cref="AudioTimeline"/>; from
/// then on each block goes to the timeline, which cuts the stream at the anchor and, since hub blocks
/// are contiguous and QPC-locked, writes the rest as-is. Called on the device's thread.
/// </summary>
internal sealed class SessionAudioTrack(SessionAudioDevice device) : IAudioPcmSubscriber
{
    private volatile AudioTimeline? _timeline;

    public SessionAudioDevice Device { get; } = device;

    public IAudioSubscription? Subscription { get; set; }

    public AudioTimeline? Timeline => _timeline;

    public void Publish(AudioTimeline timeline) => _timeline = timeline;

    public void OnPcm(in AudioBlock block)
    {
        var timeline = _timeline;
        timeline?.Push(block.StartQpc, block.Frames, block.Interleaved, block.IsSilent ? AudioPacketFlags.Silent : AudioPacketFlags.None);
    }
}
