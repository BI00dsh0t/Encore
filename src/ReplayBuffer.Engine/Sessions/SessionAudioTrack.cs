using ReplayBuffer.Core.Audio;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Audio;
using ReplayBuffer.Media;

namespace ReplayBuffer.Engine.Sessions;

/// <summary>
/// A session's <see cref="AudioTimelineTap"/> on one of its devices, with its subscription. It holds
/// the last second of blocks before the anchor publishes its timeline, so the Track starts with the
/// device's real audio from the anchor on.
/// </summary>
internal sealed class SessionAudioTrack(SessionAudioDevice device) : AudioTimelineTap(AudioTimeline.SampleRate)
{
    public SessionAudioDevice Device { get; } = device;

    public IAudioSubscription? Subscription { get; set; }
}
