using Encore.Contracts.Settings;

namespace Encore.Core.Sessions;

/// <summary>What decides whether two specs share an encoder chain: kind, process, video, and the Tracks' names and devices.</summary>
public sealed record ChainKey(SessionKind Kind, int ProcessId, VideoSettings Video, IReadOnlyList<ChainTrack> Tracks)
{
    /// <summary>The chain a running session encodes.</summary>
    public static ChainKey Of(SessionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Of(spec.Kind, spec.Target, spec.Snapshot.Video, spec.Audio);
    }

    /// <summary>The chain a session would encode from a snapshot; null when its Track Layout can't be resolved.</summary>
    public static ChainKey? Of(SessionKind kind, CaptureTarget target, EffectiveSettings snapshot)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(snapshot);
        SessionAudio audio;
        try
        {
            audio = SessionAudio.FromLayout(snapshot.TrackLayout);
        }
        catch (Exception)
        {
            return null;
        }

        return Of(kind, target, snapshot.Video, audio);
    }

    /// <summary>Whether two specs encode on one chain: equal keys and the same mic mute inputs.</summary>
    public static bool SameChain(SessionSpec a, SessionSpec b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Of(a) == Of(b) && GatedInputs(a.Audio).SequenceEqual(GatedInputs(b.Audio));
    }

    public bool Equals(ChainKey? other) =>
        other is not null
        && Kind == other.Kind
        && ProcessId == other.ProcessId
        && Video == other.Video
        && Tracks.SequenceEqual(other.Tracks);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Kind);
        hash.Add(ProcessId);
        hash.Add(Video);
        foreach (var track in Tracks)
        {
            hash.Add(track);
        }

        return hash.ToHashCode();
    }

    private static IEnumerable<(string Track, ChainDevice Device)> GatedInputs(SessionAudio audio) =>
        audio.GatedMicInputs().Select(input => (input.Track.Name, new ChainDevice(input.Device.EndpointId, input.Device.Flow)));

    private static ChainKey Of(SessionKind kind, CaptureTarget target, VideoSettings video, SessionAudio audio) => new(
        kind,
        target.ProcessId,
        video,
        [.. audio.Tracks.Select(track => new ChainTrack(track.Name, [.. track.DeviceIndexes.Select(index => Device(audio, index))]))]);

    private static ChainDevice Device(SessionAudio audio, int index) =>
        new(audio.Devices[index].EndpointId, audio.Devices[index].Flow);
}
