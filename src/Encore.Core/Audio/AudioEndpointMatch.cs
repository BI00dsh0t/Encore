using Encore.Contracts.Settings;

namespace Encore.Core.Audio;

/// <summary>When two audio endpoints are one device: the same flow, and the same endpoint id (ordinal, ignoring case).</summary>
internal static class AudioEndpointMatch
{
    public static bool Same(string idA, AudioFlow flowA, string idB, AudioFlow flowB) =>
        flowA == flowB && string.Equals(idA, idB, StringComparison.OrdinalIgnoreCase);
}
