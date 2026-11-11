namespace Encore.Core.Feedback;

/// <summary>Where Feedback Sounds play: the platform's render endpoints.</summary>
public interface IFeedbackSoundOutput
{
    /// <summary>The id of the default communications render endpoint, or <c>null</c> when there is none.</summary>
    string? DefaultEndpointId();

    /// <summary>Plays the samples on an endpoint and returns once played; throws when the endpoint can't be opened or fails.</summary>
    void Play(string endpointId, ReadOnlyMemory<float> pcm);
}
