using Encore.Core.Audio;
using Encore.Core.Sessions;
using Encore.Engine.Audio;
using Encore.Media;

namespace Encore.Engine.Sessions;

/// <summary>A session's tap on one device; it holds the last second before the anchor so the Track starts with real audio.</summary>
internal sealed class SessionAudioTrack(SessionAudioDevice device) : AudioTimelineTap(AudioTimeline.SampleRate)
{
    public SessionAudioDevice Device { get; } = device;

    public IAudioSubscription? Subscription { get; set; }
}
