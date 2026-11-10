namespace Encore.App.Settings;

/// <summary>One Track of an <see cref="AudioTracksDraft"/>: its id (kept across renames and moves), name, mute rule and sources.</summary>
internal sealed class AudioTracksDraftTrack(Guid id, string name, bool muteAffected, IEnumerable<Guid> deviceKeys)
{
    public Guid Id { get; } = id;

    public string Name { get; set; } = name;

    /// <summary>Whether the Mic mute key silences this Track's Mic Devices.</summary>
    public bool MuteAffected { get; set; } = muteAffected;

    /// <summary>The keys of the devices it records, in order.</summary>
    public List<Guid> DeviceKeys { get; } = [.. deviceKeys];
}
