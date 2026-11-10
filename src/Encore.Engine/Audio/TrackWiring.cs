using Encore.Core.Sessions;
using Encore.Media;

namespace Encore.Engine.Audio;

/// <summary>The device-to-Track fan-out a capture session and a meter watch share. Wiring time only.</summary>
internal static class TrackWiring
{
    /// <summary>
    /// The sink for device <paramref name="device"/>: each Track's mixer input for it, where
    /// <paramref name="inputs"/>[t] lists Track t's mixer inputs as device indexes, in Track order.
    /// Mic mute gates a Mic Device's input in a <c>muteAffected</c> Track. One Track's input is
    /// returned as-is; several are an <see cref="AudioFanOut"/>.
    /// </summary>
    public static IAudioSampleSink DeviceSink(int device, bool isMic, IReadOnlyList<SessionTrack> tracks, IReadOnlyList<IReadOnlyList<int>> inputs,
        IReadOnlyList<TrackMixer> mixers, MicMuteSchedule schedule, long frequency, long anchorQpc)
    {
        var sinks = new List<IAudioSampleSink>();
        for (var t = 0; t < tracks.Count; t++)
        {
            var input = IndexOf(inputs[t], device);
            if (input >= 0)
            {
                // Mic mute acts before the mix, and only on a Mic Device in a Track it affects.
                var sink = mixers[t].Input(input);
                sinks.Add(isMic && tracks[t].MuteAffected
                    ? new MicMuteGate(sink, schedule, frequency, anchorQpc)
                    : sink);
            }
        }

        return sinks.Count == 1 ? sinks[0] : new AudioFanOut(sinks);
    }

    private static int IndexOf(IReadOnlyList<int> indexes, int value)
    {
        for (var i = 0; i < indexes.Count; i++)
        {
            if (indexes[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
