using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>Shared rules of the Audio Tracks rows.</summary>
internal static class AudioLevels
{
    /// <summary>A level bar, 0..1: a linear peak on the −60..0 dBFS scale.</summary>
    public static double Bar(float peak) => peak <= 0 ? 0 : Math.Clamp(((20 * Math.Log10(peak)) + 60) / 60, 0, 1);

    /// <summary>The kind caption (EXPERIENCE.md's glossary).</summary>
    public static string Kind(AudioFlow flow, bool isMic) => flow == AudioFlow.Output ? "playback" : isMic ? "microphone" : "input";
}
