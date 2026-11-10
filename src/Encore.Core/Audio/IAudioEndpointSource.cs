using Encore.Contracts.Messages;

namespace Encore.Core.Audio;

/// <summary>The PC's audio endpoints: the present ones of both flows, and a signal when they change.</summary>
public interface IAudioEndpointSource
{
    /// <summary>Every active endpoint of both flows, with each flow's Windows default marked; throws when they can't be listed.</summary>
    IReadOnlyList<AudioDevice> ListPresent();

    /// <summary>The endpoints or a default changed; raised on any thread, once per burst. Handlers must not block.</summary>
    event Action? EndpointsChanged;
}
