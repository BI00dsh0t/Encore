namespace Encore.Core.Feedback;

/// <summary>An output that plays nothing, on a placeholder endpoint no layout holds; test instances get it.</summary>
public sealed class SilentFeedbackSoundOutput : IFeedbackSoundOutput
{
    public static SilentFeedbackSoundOutput Instance { get; } = new();

    public string? DefaultEndpointId() => "silent";

    public void Play(string endpointId, ReadOnlyMemory<float> pcm)
    {
    }
}
