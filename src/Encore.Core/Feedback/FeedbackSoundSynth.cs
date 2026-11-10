using Encore.Contracts.Messages;

namespace Encore.Core.Feedback;

/// <summary>
/// The five built-in Feedback Sounds, rendered by deterministic code and cached on first use:
/// 48 kHz interleaved stereo float, each under 600 ms with a short fade in and out, and a peak of
/// exactly <see cref="PeakDbfs"/>. Nothing ships as a file and the samples are identical every run.
/// </summary>
public static class FeedbackSoundSynth
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>The peak level of every sound.</summary>
    public const double PeakDbfs = -12;

    private const double FadeInSeconds = 0.005;
    private const double FadeOutSeconds = 0.025;

    private static readonly float[]?[] Cache = new float[Enum.GetValues<FeedbackSound>().Length][];

    /// <summary>The samples of <paramref name="sound"/>; the same array every call, which callers must not change.</summary>
    public static ReadOnlyMemory<float> Render(FeedbackSound sound)
    {
        var index = (int)sound;
        if ((uint)index >= (uint)Cache.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(sound), sound, null);
        }

        var samples = Volatile.Read(ref Cache[index]);
        if (samples is null)
        {
            samples = Build(sound);
            Volatile.Write(ref Cache[index], samples);
        }

        return samples;
    }

    private static float[] Build(FeedbackSound sound)
    {
        // Each note: its start, length and pitch in seconds and Hz, and its harmonic recipe.
        Note[] notes = sound switch
        {
            // A rising two-note chime: E5 then A5, with a soft 2nd harmonic.
            FeedbackSound.ClipSaved => [new(0, 0.16, 659.26, Timbre.Chime), new(0.15, 0.30, 880.00, Timbre.Chime)],

            // A low descending double buzz: 220 Hz then 165 Hz, odd harmonics.
            FeedbackSound.SaveFailed => [new(0, 0.18, 220, Timbre.Buzz), new(0.24, 0.22, 165, Timbre.Buzz)],

            // One high note, C6.
            FeedbackSound.RecordingStarted => [new(0, 0.22, 1046.50, Timbre.Chime)],

            // One low note, E4.
            FeedbackSound.RecordingStopped => [new(0, 0.26, 329.63, Timbre.Chime)],

            // Three short 196 Hz pulses.
            FeedbackSound.DiskFull => [new(0, 0.09, 196, Timbre.Buzz), new(0.15, 0.09, 196, Timbre.Buzz), new(0.30, 0.09, 196, Timbre.Buzz)],
            _ => throw new ArgumentOutOfRangeException(nameof(sound), sound, null),
        };

        var seconds = notes.Max(n => n.Start + n.Length);
        var frames = (int)Math.Ceiling(seconds * SampleRate);
        var mono = new double[frames];
        foreach (var note in notes)
        {
            Add(mono, note);
        }

        var peak = mono.Max(Math.Abs);
        var scale = Math.Pow(10, PeakDbfs / 20) / peak;
        var samples = new float[frames * Channels];
        for (var i = 0; i < frames; i++)
        {
            samples[Channels * i] = samples[(Channels * i) + 1] = (float)(mono[i] * scale);
        }

        return samples;
    }

    private static void Add(double[] mono, Note note)
    {
        var first = (int)Math.Round(note.Start * SampleRate);
        var count = (int)Math.Round(note.Length * SampleRate);
        var fadeIn = FadeInSeconds * SampleRate;
        var fadeOut = FadeOutSeconds * SampleRate;
        for (var i = 0; i < count && first + i < mono.Length; i++)
        {
            var envelope = Math.Min(1, Math.Min((i + 1) / fadeIn, (count - i) / fadeOut));
            var phase = 2 * Math.PI * note.Hz * i / SampleRate;
            double value;
            if (note.Timbre == Timbre.Chime)
            {
                // A gentle decay; sin x + 0.3 sin 2x crosses zero only where sin x does.
                envelope *= Math.Exp(-3.0 * i / count);
                value = Math.Sin(phase) + (0.3 * Math.Sin(2 * phase));
            }
            else
            {
                // A band-limited square: odd harmonics at 1/n, which keeps the fundamental's zero crossings.
                value = Math.Sin(phase) + (Math.Sin(3 * phase) / 3) + (Math.Sin(5 * phase) / 5) + (Math.Sin(7 * phase) / 7);
            }

            mono[first + i] += envelope * value;
        }
    }

    private enum Timbre
    {
        Chime,
        Buzz,
    }

    private readonly record struct Note(double Start, double Length, double Hz, Timbre Timbre);
}
