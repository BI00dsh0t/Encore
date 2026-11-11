namespace Encore.Platform.Audio.Tests;

/// <summary>Whether this PC has a real output and input device; a hosted CI runner has neither.</summary>
public static class AudioDevices
{
    private static readonly Lazy<bool> Present = new(
        () => AudioEndpoints.List(AudioFlow.Output).Count > 0 && AudioEndpoints.List(AudioFlow.Input).Count > 0);

    /// <summary>Skips the test unless this PC has at least one audio output and one audio input device.</summary>
    public static void Require() => Assert.SkipUnless(Present.Value, "no audio output and input device here");
}
