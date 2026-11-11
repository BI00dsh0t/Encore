using Encore.Contracts.Messages;
using Encore.Core.Feedback;

namespace Encore.Platform.Audio.Tests;

/// <summary>The real WASAPI output, on this PC's default communications endpoint; you should hear five sounds.</summary>
public sealed class FeedbackSoundOutputTests
{
    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void EachSoundPlaysOnTheDefaultCommunicationsEndpoint()
    {
        var output = new FeedbackSoundOutput();
        var endpoint = output.DefaultEndpointId();
        Assert.NotNull(endpoint);

        foreach (var sound in Enum.GetValues<FeedbackSound>())
        {
            output.Play(endpoint, FeedbackSoundSynth.Render(sound));
            Thread.Sleep(200);
        }
    }
}
