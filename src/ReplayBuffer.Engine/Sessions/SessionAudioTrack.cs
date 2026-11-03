using ReplayBuffer.Core.Audio;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Audio;

namespace ReplayBuffer.Engine.Sessions;

/// <summary>A session's <see cref="AudioTimelineTap"/> on one of its devices, with its subscription.</summary>
internal sealed class SessionAudioTrack(SessionAudioDevice device) : AudioTimelineTap
{
    public SessionAudioDevice Device { get; } = device;

    public IAudioSubscription? Subscription { get; set; }
}
