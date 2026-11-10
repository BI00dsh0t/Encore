using System.Diagnostics;

namespace Encore.Core.Sessions;

/// <summary>The Save Cooldown: a save press inside it is ignored. It runs in Hotkey QPC from the last accepted press.</summary>
internal sealed class SaveCooldown
{
    // The Hotkey QPC of the last accepted save press; a new session keeps it.
    private long? _acceptedAt;

    /// <summary>Accepts the press and returns null; inside the cooldown, returns how long ago the last accepted press was.</summary>
    public TimeSpan? Accept(long qpc, long cooldownTicks)
    {
        if (_acceptedAt is { } from && qpc - from < cooldownTicks)
        {
            return TimeSpan.FromSeconds((qpc - from) / (double)Stopwatch.Frequency);
        }

        _acceptedAt = qpc;
        return null;
    }
}
