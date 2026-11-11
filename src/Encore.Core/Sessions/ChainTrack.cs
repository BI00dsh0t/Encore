namespace Encore.Core.Sessions;

/// <summary>One Track of a <see cref="ChainKey"/>: its name and the devices it mixes, in order.</summary>
public sealed record ChainTrack(string Name, IReadOnlyList<ChainDevice> Devices)
{
    public bool Equals(ChainTrack? other) =>
        other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal) && Devices.SequenceEqual(other.Devices);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Name, StringComparer.Ordinal);
        foreach (var device in Devices)
        {
            hash.Add(device);
        }

        return hash.ToHashCode();
    }
}
